using UrlCrawler.Domain;

namespace UrlCrawler.Tests;

public sealed class DomainEntityTests
{
    [Fact]
    public void Entities_initialize_ids_timestamps_collections_and_job_defaults()
    {
        var before = DateTimeOffset.UtcNow;
        var job = new CrawlJob { RootUrl = "https://example.com", MaxDepth = 2 };
        var page = new CrawledPage { Url = "https://example.com", UrlKey = "key" };
        var link = new CrawledLink { TargetUrl = "https://example.com" };
        var outbox = new OutboxMessage { Type = "type", Payload = "payload" };

        Assert.NotEqual(Guid.Empty, job.Id);
        Assert.Equal(CrawlJobStatus.Queued, job.Status);
        Assert.InRange(job.CreatedAt, before, DateTimeOffset.UtcNow);
        Assert.Null(job.StartedAt);
        Assert.Null(job.CompletedAt);
        Assert.Null(job.FailureReason);
        Assert.Empty(job.Pages);
        Assert.NotEqual(Guid.Empty, page.Id);
        Assert.InRange(page.CrawledAt, before, DateTimeOffset.UtcNow);
        Assert.Empty(page.Links);
        Assert.Equal(0, page.Depth);
        Assert.Null(page.Title);
        Assert.Null(page.Error);
        Assert.Equal(CrawledPageStatus.Completed, page.Status);
        Assert.NotEqual(Guid.Empty, link.Id);
        Assert.Equal(Guid.Empty, link.PageId);
        Assert.Equal("https://example.com", link.TargetUrl);
        Assert.Null(link.AnchorText);
        Assert.False(link.IsInternal);
        Assert.NotEqual(Guid.Empty, outbox.Id);
        Assert.InRange(outbox.CreatedAt, before, DateTimeOffset.UtcNow);
        Assert.Null(outbox.PublishedAt);
        Assert.Equal(new CrawlRequested(job.Id), new CrawlRequested(job.Id));
    }
}
