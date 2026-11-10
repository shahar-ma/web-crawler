using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using UrlCrawler.Domain;
using UrlCrawler.Infrastructure;
using UrlCrawler.Worker;

namespace UrlCrawler.Tests;

public sealed class WorkerTests
{
    [Fact]
    public async Task ExecuteAsync_acknowledges_message_after_successful_processing()
    {
        var processor = new TestProcessor((_, _) => Task.CompletedTask);
        var harness = new Harness(processor);
        using var worker = harness.CreateWorker();
        using var stopping = new CancellationTokenSource();
        await worker.StartAsync(stopping.Token);
        var consumer = await harness.Consumer.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        await consumer.HandleBasicDeliver("consumer", 12, false, "", "crawl-jobs", Mock.Of<IBasicProperties>(),
            Encoding.UTF8.GetBytes(JsonMessage(Guid.NewGuid())));
        var disposition = await harness.Disposition.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await stopping.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(("ack", true, false), disposition);
        Assert.Equal(1, processor.CallCount);
        harness.Channel.Verify(channel => channel.BasicQos(0, 1, false), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_discards_invalid_json_without_requeue()
    {
        var harness = new Harness(new TestProcessor((_, _) => Task.CompletedTask));
        using var worker = harness.CreateWorker();
        using var stopping = new CancellationTokenSource();
        await worker.StartAsync(stopping.Token);
        var consumer = await harness.Consumer.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        await consumer.HandleBasicDeliver("consumer", 13, false, "", "crawl-jobs", Mock.Of<IBasicProperties>(), Encoding.UTF8.GetBytes("null"));
        var disposition = await harness.Disposition.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await stopping.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(("nack", false, false), disposition);
    }

    [Fact]
    public async Task ExecuteAsync_requeues_processing_errors()
    {
        var harness = new Harness(new TestProcessor((_, _) => throw new InvalidOperationException("failed")));
        using var worker = harness.CreateWorker();
        using var stopping = new CancellationTokenSource();
        await worker.StartAsync(stopping.Token);
        var consumer = await harness.Consumer.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        await consumer.HandleBasicDeliver("consumer", 14, false, "", "crawl-jobs", Mock.Of<IBasicProperties>(),
            Encoding.UTF8.GetBytes(JsonMessage(Guid.NewGuid())));
        var disposition = await harness.Disposition.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await stopping.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(("nack", true, false), disposition);
    }

    [Fact]
    public async Task ExecuteAsync_logs_and_retries_after_connection_failure()
    {
        var attempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness(new TestProcessor((_, _) => Task.CompletedTask),
            failConnection: true, connectionAttempt: attempt);
        using var worker = harness.CreateWorker();
        await worker.StartAsync(CancellationToken.None);

        await attempt.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);
        Assert.NotNull(harness);
    }

    [Fact]
    public async Task ExecuteAsync_requeues_delivery_canceled_during_shutdown()
    {
        var enteredProcessor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness(new TestProcessor(async (_, token) =>
        {
            enteredProcessor.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }));
        using var worker = harness.CreateWorker();
        using var stopping = new CancellationTokenSource();
        await worker.StartAsync(stopping.Token);
        var consumer = await harness.Consumer.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        var delivery = consumer.HandleBasicDeliver("consumer", 15, false, "", "crawl-jobs", Mock.Of<IBasicProperties>(),
            Encoding.UTF8.GetBytes(JsonMessage(Guid.NewGuid())));
        await enteredProcessor.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await stopping.CancelAsync();
        var disposition = await harness.Disposition.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await delivery;
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(("nack", true, false), disposition);
    }

    private static string JsonMessage(Guid id) => System.Text.Json.JsonSerializer.Serialize(new CrawlRequested(id));

    private sealed class TestProcessor(Func<CrawlRequested, CancellationToken, Task> process) : ICrawlProcessor
    {
        public int CallCount { get; private set; }

        public Task ProcessAsync(CrawlRequested message, CancellationToken cancellationToken)
        {
            CallCount++;
            return process(message, cancellationToken);
        }
    }

    private sealed class Harness
    {
        public Mock<IModel> Channel { get; } = new();
        public TaskCompletionSource<AsyncEventingBasicConsumer> Consumer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<(string, bool, bool)> Disposition { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TestProcessor _processor;
        private readonly Mock<IRabbitConnectionProvider> _connections = new();
        private readonly IServiceProvider _services;

        public Harness(TestProcessor processor, bool failConnection = false,
            TaskCompletionSource? connectionAttempt = null)
        {
            _processor = processor;
            var connection = new Mock<IConnection>();
            connection.Setup(value => value.CreateModel()).Returns(Channel.Object);
            var createConnection = _connections.Setup(value => value.CreateConnection("urlcrawler-worker"));
            if (failConnection)
                createConnection.Callback(() => connectionAttempt!.TrySetResult())
                    .Throws(new InvalidOperationException("unavailable"));
            else
                createConnection.Returns(connection.Object);
            Channel.Setup(value => value.BasicConsume(OutboxDispatcher.QueueName, false, "", false, false, null,
                    It.IsAny<IBasicConsumer>()))
                .Callback<string, bool, string, bool, bool, IDictionary<string, object>, IBasicConsumer>(
                    (_, _, _, _, _, _, consumer) => Consumer.TrySetResult((AsyncEventingBasicConsumer)consumer));
            Channel.Setup(value => value.BasicAck(It.IsAny<ulong>(), It.IsAny<bool>()))
                .Callback<ulong, bool>((_, multiple) => Disposition.TrySetResult(("ack", true, multiple)));
            Channel.Setup(value => value.BasicNack(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>()))
                .Callback<ulong, bool, bool>((_, multiple, requeue) => Disposition.TrySetResult(("nack", requeue, multiple)));
            var services = new ServiceCollection();
            services.AddScoped<ICrawlProcessor>(_ => _processor);
            _services = services.BuildServiceProvider();
        }

        public Worker.Worker CreateWorker() => new(_connections.Object,
            _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<Worker.Worker>.Instance);
    }
}
