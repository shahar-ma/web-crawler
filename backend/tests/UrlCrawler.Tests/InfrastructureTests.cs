using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UrlCrawler.Domain;
using UrlCrawler.Infrastructure;

namespace UrlCrawler.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public async Task DbContext_configures_relations_indexes_and_cascades()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(CancellationToken.None);
        var options = new DbContextOptionsBuilder<CrawlerDbContext>().UseSqlite(connection).Options;
        await using var db = new CrawlerDbContext(options);
        await db.Database.EnsureCreatedAsync(CancellationToken.None);

        var job = new CrawlJob { RootUrl = "https://example.com", MaxDepth = 1 };
        var page = new CrawledPage { JobId = job.Id, Url = job.RootUrl, UrlKey = "key" };
        page.Links.Add(new CrawledLink { TargetUrl = "https://example.com/next" });
        job.Pages.Add(page);
        db.Jobs.Add(job);
        db.Outbox.Add(new OutboxMessage { Type = "event", Payload = "{}" });
        await db.SaveChangesAsync(CancellationToken.None);

        Assert.Equal(1, await db.Pages.CountAsync(CancellationToken.None));
        Assert.Equal(1, await db.Links.CountAsync(CancellationToken.None));
        Assert.Equal(1, await db.Outbox.CountAsync(CancellationToken.None));
        Assert.DoesNotContain("DomainLinkRatio", db.Model.FindEntityType(typeof(CrawledPage))!.GetProperties()
            .Select(property => property.Name));

        db.Jobs.Remove(job);
        await db.SaveChangesAsync(CancellationToken.None);
        Assert.Equal(0, await db.Pages.CountAsync(CancellationToken.None));
        Assert.Equal(0, await db.Links.CountAsync(CancellationToken.None));
    }

    [Fact]
    public void Infrastructure_registration_adds_database_provider_and_connection_provider()
    {
        var services = new ServiceCollection();

        services.AddCrawlerInfrastructure("Data Source=localhost;Initial Catalog=crawler");

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(CrawlerDbContext));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(RabbitConnectionProvider));
    }

    [Fact]
    public void Outbox_registration_adds_hosted_service()
    {
        var services = new ServiceCollection();

        services.AddOutboxDispatcher();

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService));
    }

    [Fact]
    public void Rabbit_connection_provider_requires_uri_configuration()
    {
        var configuration = new ConfigurationBuilder().Build();

        var exception = Assert.Throws<InvalidOperationException>(() => new RabbitConnectionProvider(configuration));

        Assert.Contains("RabbitMq:Uri", exception.Message);
    }

    [Fact]
    public void Rabbit_connection_provider_rejects_malformed_uri()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["RabbitMq:Uri"] = "not a uri" }).Build();

        Assert.Throws<UriFormatException>(() => new RabbitConnectionProvider(configuration));
    }
}
