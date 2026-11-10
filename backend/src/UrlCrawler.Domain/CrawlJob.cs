namespace UrlCrawler.Domain;

public enum CrawlJobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Canceled
}

public enum CrawledPageStatus
{
    Completed,
    Failed,
    Skipped
}

public sealed class CrawlJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string RootUrl { get; set; }
    public int MaxDepth { get; set; }
    public CrawlJobStatus Status { get; set; } = CrawlJobStatus.Queued;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
    public ICollection<CrawledPage> Pages { get; set; } = new List<CrawledPage>();
}

public sealed class CrawledPage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public required string Url { get; set; }
    public required string UrlKey { get; set; }
    public int Depth { get; set; }
    public string? Title { get; set; }
    public CrawledPageStatus Status { get; set; }
    public string? Error { get; set; }
    public int TotalLinks { get; set; }
    public int InternalLinks { get; set; }
    public double DomainLinkRatio => TotalLinks == 0 ? 0 : (double)InternalLinks / TotalLinks;
    public DateTimeOffset CrawledAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<CrawledLink> Links { get; set; } = new List<CrawledLink>();
}

public sealed class CrawledLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PageId { get; set; }
    public required string TargetUrl { get; set; }
    public string? AnchorText { get; set; }
    public bool IsInternal { get; set; }
}

public sealed class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Type { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
}

public sealed record CrawlRequested(Guid JobId);
