using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UrlCrawler.Domain;
using UrlCrawler.Infrastructure;

namespace UrlCrawler.Tests;

public sealed class ApiIntegrationTests
{
    [Fact]
    public async Task CreateJob_validates_input_then_creates_job_and_outbox_event()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var invalidUrl = await client.PostAsJsonAsync("/api/jobs", new { Url = "http://localhost", MaxDepth = 1 }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, invalidUrl.StatusCode);
        var negativeDepth = await client.PostAsJsonAsync("/api/jobs", new { Url = "https://example.com", MaxDepth = -1 }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, negativeDepth.StatusCode);
        var invalidDepth = await client.PostAsJsonAsync("/api/jobs", new { Url = "https://example.com", MaxDepth = 4 }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, invalidDepth.StatusCode);
        var invalidLength = await client.PostAsJsonAsync("/api/jobs", new
        {
            Url = "https://example.com/" + new string('a', 2050),
            MaxDepth = 1
        }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, invalidLength.StatusCode);

        var response = await client.PostAsJsonAsync("/api/jobs", new { Url = "https://example.com:443/start#top" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.StartsWith("/api/jobs/", response.Headers.Location!.OriginalString);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        var id = body.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("https://example.com/start", body.RootElement.GetProperty("rootUrl").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("maxDepth").GetInt32());
        Assert.Equal("Queued", body.RootElement.GetProperty("status").GetString());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
        Assert.Equal(1, await db.Jobs.CountAsync(CancellationToken.None));
        var message = await db.Outbox.SingleAsync(CancellationToken.None);
        Assert.Equal(OutboxDispatcher.EventType, message.Type);
        Assert.Equal(id, JsonDocument.Parse(message.Payload).RootElement.GetProperty("JobId").GetGuid());
    }

    [Fact]
    public async Task Jobs_list_get_details_and_page_links_apply_not_found_and_pagination_rules()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
        var older = CreateJob("https://example.com/older", DateTimeOffset.UtcNow.AddMinutes(-1));
        var newer = CreateJob("https://example.com/newer", DateTimeOffset.UtcNow);
        var page = new CrawledPage
        {
            JobId = newer.Id,
            Url = newer.RootUrl,
            UrlKey = "page-key",
            Status = CrawledPageStatus.Completed,
            TotalLinks = 3,
            InternalLinks = 2
        };
        page.Links.Add(new CrawledLink { TargetUrl = "https://example.com/external", IsInternal = false });
        page.Links.Add(new CrawledLink { TargetUrl = "https://example.com/z", IsInternal = true });
        page.Links.Add(new CrawledLink { TargetUrl = "https://example.com/a", IsInternal = true });
        newer.Pages.Add(page);
        db.Jobs.AddRange(older, newer);
        await db.SaveChangesAsync(CancellationToken.None);

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/jobs?page=-1&pageSize=50", CancellationToken.None));
        Assert.Equal(2, list.RootElement.GetArrayLength());
        Assert.Equal(newer.Id, list.RootElement[0].GetProperty("id").GetGuid());
        Assert.Equal(1, list.RootElement[0].GetProperty("pagesCrawled").GetInt32());
        using var pageList = JsonDocument.Parse(await client.GetStringAsync("/api/jobs?page=0&pageSize=0", CancellationToken.None));
        Assert.Single(pageList.RootElement.EnumerateArray());

        var missing = await client.GetAsync($"/api/jobs/{Guid.NewGuid()}", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var details = JsonDocument.Parse(await client.GetStringAsync($"/api/jobs/{newer.Id}", CancellationToken.None));
        Assert.Equal("Completed", details.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, details.RootElement.GetProperty("pagesCrawled").GetInt32());

        var missingLinks = await client.GetAsync($"/api/jobs/{newer.Id}/pages/{Guid.NewGuid()}/links", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, missingLinks.StatusCode);
        using var links = JsonDocument.Parse(await client.GetStringAsync($"/api/jobs/{newer.Id}/pages/{page.Id}/links?page=-1&pageSize=100", CancellationToken.None));
        Assert.Equal(3, links.RootElement.GetArrayLength());
        Assert.True(links.RootElement[0].GetProperty("isInternal").GetBoolean());
        Assert.Equal("https://example.com/a", links.RootElement[0].GetProperty("targetUrl").GetString());
        using var limitedLinks = JsonDocument.Parse(await client.GetStringAsync($"/api/jobs/{newer.Id}/pages/{page.Id}/links?page=0&pageSize=0", CancellationToken.None));
        Assert.Single(limitedLinks.RootElement.EnumerateArray());
        Assert.Equal(older.Id, (await db.Jobs.SingleAsync(job => job.RootUrl == older.RootUrl, CancellationToken.None)).Id);
    }

    [Fact]
    public async Task Cancel_endpoint_cancels_active_jobs_and_preserves_terminal_statuses()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
        var queued = CreateJob("https://example.com/queued", DateTimeOffset.UtcNow, CrawlJobStatus.Queued);
        var running = CreateJob("https://example.com/running", DateTimeOffset.UtcNow, CrawlJobStatus.Running);
        var completed = CreateJob("https://example.com/completed", DateTimeOffset.UtcNow, CrawlJobStatus.Completed);
        db.Jobs.AddRange(queued, running, completed);
        await db.SaveChangesAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/jobs/{Guid.NewGuid()}/cancel", null, CancellationToken.None)).StatusCode);
        foreach (var active in new[] { queued, running })
        {
            var response = await client.PostAsync($"/api/jobs/{active.Id}/cancel", null, CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(CrawlJobStatus.Canceled, (await db.Jobs.AsNoTracking().SingleAsync(job => job.Id == active.Id, CancellationToken.None)).Status);
            Assert.NotNull((await db.Jobs.AsNoTracking().SingleAsync(job => job.Id == active.Id, CancellationToken.None)).CompletedAt);
        }

        await client.PostAsync($"/api/jobs/{completed.Id}/cancel", null, CancellationToken.None);
        Assert.Equal(CrawlJobStatus.Completed, (await db.Jobs.AsNoTracking().SingleAsync(job => job.Id == completed.Id, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Health_endpoint_reports_database_connectivity_and_development_serves_swagger()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        Assert.Equal("Healthy", (await client.GetStringAsync("/health", CancellationToken.None)).Trim());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/index.html", CancellationToken.None)).StatusCode);
    }

    private static CrawlJob CreateJob(string url, DateTimeOffset created, CrawlJobStatus status = CrawlJobStatus.Completed) => new()
    {
        RootUrl = url,
        MaxDepth = 1,
        CreatedAt = created,
        Status = status
    };

    private sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:CrawlerDb"] = "Server=localhost;Database=unused;User Id=sa;Password=unused;TrustServerCertificate=True",
                    ["RabbitMq:Uri"] = "amqp://guest:guest@127.0.0.1:1/"
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<CrawlerDbContext>();
                services.RemoveAll<DbContextOptions<CrawlerDbContext>>();
                services.AddDbContext<CrawlerDbContext>(options => options.UseInMemoryDatabase(_databaseName));
            });
        }
    }
}
