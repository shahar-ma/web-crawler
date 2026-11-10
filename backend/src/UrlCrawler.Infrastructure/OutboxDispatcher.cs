using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;
using UrlCrawler.Domain;

namespace UrlCrawler.Infrastructure;

public sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    IRabbitConnectionProvider connectionProvider,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    public const string QueueName = "crawl-jobs";
    public const string EventType = "crawl.requested.v1";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connection = connectionProvider.CreateConnection("urlcrawler-outbox");
                using var channel = connection.CreateModel();
                channel.QueueDeclare(QueueName, durable: true, exclusive: false, autoDelete: false);
                channel.ConfirmSelect();

                while (!stoppingToken.IsCancellationRequested)
                {
                    await DispatchBatchAsync(channel, stoppingToken);
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Outbox dispatcher failed; reconnecting shortly");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task DispatchBatchAsync(IModel channel, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
        var messages = await db.Outbox
            .Where(message => message.PublishedAt == null)
            .OrderBy(message => message.CreatedAt)
            .Take(25)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            var properties = channel.CreateBasicProperties();
            properties.Persistent = true;
            properties.Type = message.Type;
            properties.MessageId = message.Id.ToString();
            channel.BasicPublish(string.Empty, QueueName, mandatory: false, properties,
                Encoding.UTF8.GetBytes(message.Payload));
            message.PublishedAt = DateTimeOffset.UtcNow;
        }

        if (messages.Count > 0)
        {
            channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(10));
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
