using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using UrlCrawler.Domain;
using UrlCrawler.Infrastructure;

namespace UrlCrawler.Tests;

public sealed class OutboxDispatcherTests
{
    private static readonly string[] collection = ["one", "two"];

    [Fact]
    public async Task ExecuteAsync_publishes_pending_events_and_marks_them_after_confirm()
    {
        var databaseName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<CrawlerDbContext>(options => options.UseInMemoryDatabase(databaseName));
        using var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
            db.Outbox.Add(new OutboxMessage { Type = "first", Payload = "one" });
            db.Outbox.Add(new OutboxMessage { Type = "second", Payload = "two" });
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var published = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var properties = new Mock<IBasicProperties>();
        properties.SetupProperty(value => value.Persistent);
        properties.SetupProperty(value => value.Type);
        properties.SetupProperty(value => value.MessageId);
        var channel = new Mock<IModel>();
        channel.Setup(value => value.CreateBasicProperties()).Returns(properties.Object);
        channel.Setup(value => value.BasicPublish(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<IBasicProperties>(),
                It.IsAny<ReadOnlyMemory<byte>>()))
            .Callback<string, string, bool, IBasicProperties, ReadOnlyMemory<byte>>((_, _, _, _, body) =>
                published.TrySetResult(System.Text.Encoding.UTF8.GetString(body.Span)));
        var connection = new Mock<IConnection>();
        connection.Setup(value => value.CreateModel()).Returns(channel.Object);
        var connectionProvider = new Mock<IRabbitConnectionProvider>();
        connectionProvider.Setup(value => value.CreateConnection("urlcrawler-outbox")).Returns(connection.Object);
        var dispatcher = new OutboxDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            connectionProvider.Object, NullLogger<OutboxDispatcher>.Instance);
        using var stopping = new CancellationTokenSource();

        await dispatcher.StartAsync(stopping.Token);
        var payload = await published.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await stopping.CancelAsync();
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.Contains(payload, collection);
        channel.Verify(value => value.QueueDeclare(OutboxDispatcher.QueueName, true, false, false, null), Times.Once);
        channel.Verify(value => value.ConfirmSelect(), Times.Once);
        channel.Verify(value => value.WaitForConfirmsOrDie(TimeSpan.FromSeconds(10)), Times.Once);
        using var verifyScope = provider.CreateScope();
        Assert.All(await verifyScope.ServiceProvider.GetRequiredService<CrawlerDbContext>().Outbox.ToListAsync(CancellationToken.None),
            message => Assert.NotNull(message.PublishedAt));
    }

    [Fact]
    public async Task ExecuteAsync_retries_when_connection_creation_fails_and_stops_cleanly()
    {
        var services = new ServiceCollection();
        services.AddDbContext<CrawlerDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        using var provider = services.BuildServiceProvider();
        var failedConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectionProvider = new Mock<IRabbitConnectionProvider>();
        connectionProvider.Setup(value => value.CreateConnection("urlcrawler-outbox"))
            .Callback(() => failedConnection.TrySetResult())
            .Throws(new InvalidOperationException("unavailable"));
        var dispatcher = new OutboxDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            connectionProvider.Object, NullLogger<OutboxDispatcher>.Instance);
        using var stopping = new CancellationTokenSource();

        await dispatcher.StartAsync(stopping.Token);
        await failedConnection.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await stopping.CancelAsync();
        await dispatcher.StopAsync(CancellationToken.None);

        connectionProvider.Verify(value => value.CreateConnection("urlcrawler-outbox"), Times.Once);
    }
}
