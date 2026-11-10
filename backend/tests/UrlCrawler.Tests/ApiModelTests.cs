using UrlCrawler.Api;
using UrlCrawler.Domain;

namespace UrlCrawler.Tests;

public sealed class ApiModelTests
{
    [Fact]
    public void ToSummary_maps_job_fields_and_sorts_pages_by_depth_then_url_case_insensitively()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var job = new CrawlJob
        {
            Id = Guid.NewGuid(),
            RootUrl = "https://example.com/",
            MaxDepth = 2,
            Status = CrawlJobStatus.Completed,
            CreatedAt = createdAt,
            StartedAt = createdAt.AddMinutes(1),
            CompletedAt = createdAt.AddMinutes(2),
            FailureReason = "reason",
            Pages =
            [
                Page("https://example.com/z", 1),
                Page("https://example.com/B", 0),
                Page("https://example.com/a", 0)
            ]
        };

        var summary = ApiModelMapper.ToSummary(job);

        Assert.Equal(job.Id, summary.Id);
        Assert.Equal(job.RootUrl, summary.RootUrl);
        Assert.Equal(job.MaxDepth, summary.MaxDepth);
        Assert.Equal(job.Status, summary.Status);
        Assert.Equal(createdAt, summary.CreatedAt);
        Assert.Equal(job.StartedAt, summary.StartedAt);
        Assert.Equal(job.CompletedAt, summary.CompletedAt);
        Assert.Equal(job.FailureReason, summary.FailureReason);
        Assert.Equal(3, summary.PagesCrawled);
        Assert.Equal(["https://example.com/a", "https://example.com/B", "https://example.com/z"],
            summary.Pages.Select(page => page.Url));
        Assert.Equal(0.5, summary.Pages[0].DomainLinkRatio);
        Assert.NotEqual(Guid.Empty, summary.Pages[0].Id);
        Assert.Equal(0, summary.Pages[0].Depth);
        Assert.Equal("title", summary.Pages[0].Title);
        Assert.Equal(CrawledPageStatus.Completed, summary.Pages[0].Status);
        Assert.Null(summary.Pages[0].Error);
        Assert.Equal(2, summary.Pages[0].TotalLinks);
        Assert.Equal(1, summary.Pages[0].InternalLinks);
        Assert.NotEqual(default, summary.Pages[0].CrawledAt);
    }

    [Fact]
    public void Api_request_and_link_records_preserve_constructor_values()
    {
        var request = new CreateJobRequest("https://example.com", 3);
        var link = new LinkSummary("https://example.com/next", "Next", true);

        Assert.Equal("https://example.com", request.Url);
        Assert.Equal(3, request.MaxDepth);
        Assert.Equal("https://example.com/next", link.TargetUrl);
        Assert.Equal("Next", link.AnchorText);
        Assert.True(link.IsInternal);
    }

    private static CrawledPage Page(string url, int depth) => new()
    {
        Url = url,
        UrlKey = url,
        Depth = depth,
        Title = "title",
        Status = CrawledPageStatus.Completed,
        TotalLinks = 2,
        InternalLinks = 1,
        CrawledAt = DateTimeOffset.UtcNow
    };
}
