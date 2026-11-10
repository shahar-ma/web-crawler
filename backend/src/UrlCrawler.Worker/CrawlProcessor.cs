using AngleSharp.Html.Parser;
using Microsoft.EntityFrameworkCore;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using UrlCrawler.Domain;
using UrlCrawler.Infrastructure;

namespace UrlCrawler.Worker;

public sealed class CrawlProcessor(
    CrawlerDbContext db,
    HttpClient httpClient,
    ILogger<CrawlProcessor> logger) : ICrawlProcessor
{
    private const int MaxPagesPerJob = 25;
    private const int MaxLinksPerPage = 250;
    private const int MaxHtmlBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan StaleJobAge = TimeSpan.FromMinutes(10);

    public async Task ProcessAsync(CrawlRequested message, CancellationToken cancellationToken)
    {
        if (!await TryClaimJobAsync(message.JobId, cancellationToken))
            return;

        try
        {
            var job = await LoadJobAsync(message.JobId, cancellationToken);
            var root = new Uri(job.RootUrl);
            var state = await LoadCrawlStateAsync(job, root, cancellationToken);
            var processed = await CrawlPendingPagesAsync(job, root, state, cancellationToken);
            if (processed.HasValue)
                await CompleteJobAsync(job.Id, processed.Value, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = await ResetClaimedJobAsync(message.JobId);
            throw;
        }
        catch (Exception exception)
        {
            await FailClaimedJobAsync(message.JobId, exception, cancellationToken);
        }
    }

    private async Task<bool> TryClaimJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now - StaleJobAge;
        var claimed = await db.Jobs
            .Where(job => job.Id == jobId &&
                (job.Status == CrawlJobStatus.Queued ||
                 (job.Status == CrawlJobStatus.Running && job.StartedAt < staleBefore)))
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(job => job.Status, CrawlJobStatus.Running)
                .SetProperty(job => job.StartedAt, (DateTimeOffset?)now)
                .SetProperty(job => job.FailureReason, (string?)null), cancellationToken);
        return claimed > 0;
    }

    private Task<CrawlJob> LoadJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        db.Jobs.AsNoTracking().SingleAsync(item => item.Id == jobId, cancellationToken);

    private async Task<CrawlState> LoadCrawlStateAsync(
        CrawlJob job, Uri root, CancellationToken cancellationToken)
    {
        var existingPages = await db.Pages.AsNoTracking().Include(page => page.Links)
            .Where(page => page.JobId == job.Id).ToListAsync(cancellationToken);
        var seen = new HashSet<string>(
            existingPages.Select(page => PublicUrlPolicy.Normalize(new Uri(page.Url))),
            StringComparer.OrdinalIgnoreCase);
        var pending = CreatePendingPages(existingPages, root, job.MaxDepth, seen);
        return new CrawlState(pending, seen, existingPages.Count);
    }

    private static Queue<(Uri Url, int Depth)> CreatePendingPages(
        List<CrawledPage> existingPages, Uri root, int maxDepth, HashSet<string> seen)
    {
        var pending = new Queue<(Uri Url, int Depth)>();
        if (existingPages.Count == 0)
        {
            seen.Add(PublicUrlPolicy.Normalize(root));
            pending.Enqueue((root, 0));
            return pending;
        }

        foreach (var page in existingPages.Where(page =>
                     page.Status == CrawledPageStatus.Completed && page.Depth < maxDepth))
            EnqueueInternalLinks(page.Links, page.Depth + 1, pending, seen);
        return pending;
    }

    private async Task<int?> CrawlPendingPagesAsync(
        CrawlJob job, Uri root, CrawlState state, CancellationToken cancellationToken)
    {
        var processed = state.Processed;
        while (state.Pending.Count > 0 && processed < MaxPagesPerJob)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await IsJobRunningAsync(job.Id, cancellationToken))
                return null;

            var (url, depth) = state.Pending.Dequeue();
            var page = await CrawlPageAsync(job.Id, root, url, depth, cancellationToken);
            db.Pages.Add(page);
            await db.SaveChangesAsync(cancellationToken);
            processed++;
            EnqueueNextPages(job, page, depth, state);
        }

        return processed;
    }

    private Task<bool> IsJobRunningAsync(Guid jobId, CancellationToken cancellationToken) =>
        db.Jobs.AsNoTracking().AnyAsync(
            item => item.Id == jobId && item.Status == CrawlJobStatus.Running, cancellationToken);

    private static void EnqueueNextPages(CrawlJob job, CrawledPage page, int depth, CrawlState state)
    {
        if (depth < job.MaxDepth && page.Status == CrawledPageStatus.Completed)
            EnqueueInternalLinks(page.Links, depth + 1, state.Pending, state.Seen);
    }

    private static void EnqueueInternalLinks(
        IEnumerable<CrawledLink> links, int depth, Queue<(Uri Url, int Depth)> pending, HashSet<string> seen)
    {
        foreach (var link in links.Where(link => link.IsInternal))
        {
            if (Uri.TryCreate(link.TargetUrl, UriKind.Absolute, out var child) &&
                seen.Add(PublicUrlPolicy.Normalize(child)))
                pending.Enqueue((child, depth));
        }
    }

    private async Task CompleteJobAsync(Guid jobId, int processed, CancellationToken cancellationToken)
    {
        var completedAt = DateTimeOffset.UtcNow;
        await db.Jobs.Where(item => item.Id == jobId && item.Status == CrawlJobStatus.Running)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(item => item.Status, CrawlJobStatus.Completed)
                .SetProperty(item => item.CompletedAt, (DateTimeOffset?)completedAt), cancellationToken);
        logger.LogInformation("Crawl job {JobId} completed with {PageCount} pages", jobId, processed);
    }

    private Task<int> ResetClaimedJobAsync(Guid jobId) => db.Jobs
        .Where(job => job.Id == jobId && job.Status == CrawlJobStatus.Running)
        .ExecuteUpdateAsync(updates => updates
            .SetProperty(job => job.Status, CrawlJobStatus.Queued)
            .SetProperty(job => job.StartedAt, (DateTimeOffset?)null), CancellationToken.None);

    private async Task FailClaimedJobAsync(Guid jobId, Exception exception, CancellationToken cancellationToken)
    {
        var completedAt = DateTimeOffset.UtcNow;
        var failureReason = exception.Message.Length > 500
            ? exception.Message[..500]
            : exception.Message;
        await db.Jobs.Where(job => job.Id == jobId && job.Status == CrawlJobStatus.Running)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(job => job.Status, CrawlJobStatus.Failed)
                .SetProperty(job => job.CompletedAt, (DateTimeOffset?)completedAt)
                .SetProperty(job => job.FailureReason, failureReason), cancellationToken);
        logger.LogError(exception, "Crawl job {JobId} failed", jobId);
    }

    private sealed record CrawlState(
        Queue<(Uri Url, int Depth)> Pending, HashSet<string> Seen, int Processed);

    private async Task<CrawledPage> CrawlPageAsync(
        Guid jobId,
        Uri root,
        Uri url,
        int depth,
        CancellationToken cancellationToken)
    {
        var page = new CrawledPage
        {
            JobId = jobId,
            Url = PublicUrlPolicy.Normalize(url),
            UrlKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PublicUrlPolicy.Normalize(url)))),
            Depth = depth
        };

        try
        {
            await PublicUrlPolicy.EnsurePublicHostAsync(url, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("UrlCrawlerDemo/1.0 (+https://github.com/WMalka/UrlCrawler)");
            using var response = await httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                page.Status = CrawledPageStatus.Failed;
                page.Error = $"HTTP {(int)response.StatusCode}";
                return page;
            }
            if (response.Content.Headers.ContentType?.MediaType is not { } mediaType ||
                !mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
            {
                page.Status = CrawledPageStatus.Skipped;
                page.Error = "The response is not HTML.";
                return page;
            }
            if (response.Content.Headers.ContentLength > MaxHtmlBytes)
            {
                page.Status = CrawledPageStatus.Skipped;
                page.Error = "The HTML document exceeds the 2 MB limit.";
                return page;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var bounded = new MemoryStream();
            var buffer = new byte[8192];
            while (bounded.Length <= MaxHtmlBytes)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;
                await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (bounded.Length > MaxHtmlBytes)
            {
                page.Status = CrawledPageStatus.Skipped;
                page.Error = "The HTML document exceeds the 2 MB limit.";
                return page;
            }

            bounded.Position = 0;
            using var reader = new StreamReader(bounded);
            var document = await new HtmlParser().ParseDocumentAsync(await reader.ReadToEndAsync(cancellationToken));
            page.Title = Limit(document.Title?.Trim(), 500);
            page.Links = [.. document.QuerySelectorAll("a[href]")
                .Take(MaxLinksPerPage)
                .Select(anchor => CreateLink(anchor.GetAttribute("href"), anchor.TextContent, url, root))
                .Where(link => link is not null)
                .Cast<CrawledLink>()];
            page.TotalLinks = page.Links.Count;
            page.InternalLinks = page.Links.Count(link => link.IsInternal);
            page.Status = CrawledPageStatus.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                           InvalidOperationException or SocketException)
        {
            page.Status = CrawledPageStatus.Failed;
            page.Error = Limit(exception.Message, 500);
        }

        return page;
    }

    private static CrawledLink? CreateLink(string? href, string? anchorText, Uri pageUrl, Uri root)
    {
        if (!Uri.TryCreate(pageUrl, href, out var target) ||
            (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
            return null;
        var targetUrl = PublicUrlPolicy.Normalize(target);
        if (targetUrl.Length > 2048)
            return null;
        return new CrawledLink
        {
            TargetUrl = targetUrl,
            AnchorText = Limit(anchorText?.Trim(), 500),
            IsInternal = PublicUrlPolicy.IsSameOrigin(root, target)
        };
    }

    private static string? Limit(string? value, int length)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Length <= length ? value : value[..length];
    }
}
