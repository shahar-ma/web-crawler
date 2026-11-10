using UrlCrawler.Domain;

namespace UrlCrawler.Api;

public sealed record CreateJobRequest(string Url, int? MaxDepth);

public sealed record PageSummary(
    Guid Id,
    string Url,
    int Depth,
    string? Title,
    CrawledPageStatus Status,
    string? Error,
    int TotalLinks,
    int InternalLinks,
    double DomainLinkRatio,
    DateTimeOffset CrawledAt);

public sealed record JobSummary(
    Guid Id,
    string RootUrl,
    int MaxDepth,
    CrawlJobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason,
    int PagesCrawled,
    IReadOnlyList<PageSummary> Pages);

public sealed record LinkSummary(string TargetUrl, string? AnchorText, bool IsInternal);

public static class ApiModelMapper
{
    public static JobSummary ToSummary(CrawlJob job) =>
        new(job.Id, job.RootUrl, job.MaxDepth, job.Status, job.CreatedAt, job.StartedAt,
            job.CompletedAt, job.FailureReason, job.Pages.Count,
            [.. job.Pages.OrderBy(page => page.Depth).ThenBy(page => page.Url, StringComparer.OrdinalIgnoreCase)
                .Select(page => new PageSummary(page.Id, page.Url, page.Depth, page.Title, page.Status,
                    page.Error, page.TotalLinks, page.InternalLinks, page.DomainLinkRatio, page.CrawledAt))]);
}
