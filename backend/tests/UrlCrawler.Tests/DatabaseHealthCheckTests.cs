using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using UrlCrawler.Api;
using UrlCrawler.Infrastructure;

namespace UrlCrawler.Tests;

public sealed class DatabaseHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_returns_healthy_when_database_is_reachable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(CancellationToken.None);
        await using var db = new CrawlerDbContext(new DbContextOptionsBuilder<CrawlerDbContext>()
            .UseSqlite(connection).Options);
        var check = new DatabaseHealthCheck(db);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("SQL Server is reachable.", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_returns_unhealthy_when_database_cannot_connect()
    {
        await using var db = new CrawlerDbContext(new DbContextOptionsBuilder<CrawlerDbContext>()
            .UseSqlite("Data Source=missing\\crawler.db;Mode=ReadOnly").Options);
        var check = new DatabaseHealthCheck(db);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("SQL Server is not reachable.", result.Description);
    }
}
