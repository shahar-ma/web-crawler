using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;
using UrlCrawler.Domain;
using UrlCrawler.Infrastructure;

namespace UrlCrawler.Worker;

public sealed class Worker(
    IRabbitConnectionProvider connectionProvider,
    IServiceScopeFactory scopeFactory,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connection = connectionProvider.CreateConnection("urlcrawler-worker");
                using var channel = connection.CreateModel();
                channel.QueueDeclare(OutboxDispatcher.QueueName, durable: true, exclusive: false, autoDelete: false);
                channel.BasicQos(0, 1, global: false);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.Received += async (_, delivery) =>
                {
                    try
                    {
                        var message = JsonSerializer.Deserialize<CrawlRequested>(Encoding.UTF8.GetString(delivery.Body.Span)) ?? throw new JsonException("The crawl request payload is empty.");
                        await using var scope = scopeFactory.CreateAsyncScope();
                        var processor = scope.ServiceProvider.GetRequiredService<ICrawlProcessor>();
                        await processor.ProcessAsync(message, stoppingToken);
                        channel.BasicAck(delivery.DeliveryTag, multiple: false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: true);
                    }
                    catch (JsonException exception)
                    {
                        logger.LogError(exception, "Discarding malformed crawl event");
                        channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: false);
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "Crawl event processing failed; requeuing");
                        channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: true);
                    }
                };

                channel.BasicConsume(OutboxDispatcher.QueueName, autoAck: false, consumer);
                logger.LogInformation("Crawler worker is consuming {Queue}", OutboxDispatcher.QueueName);
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.ConnectionShutdown += (_, _) => disconnected.TrySetResult();
                await disconnected.Task.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "RabbitMQ consumer stopped; reconnecting shortly");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
