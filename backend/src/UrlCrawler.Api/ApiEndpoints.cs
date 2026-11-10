using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using UrlCrawler.Domain;
using UrlCrawler.Infrastructure;

namespace UrlCrawler.Api;

internal static class ApiEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/jobs", (int? page, int? pageSize, CrawlerDbContext db, CancellationToken ct) =>
            ListJobsAsync(page, pageSize, db, ct)).WithName("ListJobs");
        app.MapPost("/api/jobs", (CreateJobRequest request, CrawlerDbContext db, CancellationToken ct) =>
            CreateJobAsync(request, db, ct)).WithName("CreateJob");
        app.MapGet("/api/jobs/{id:guid}", (Guid id, CrawlerDbContext db, CancellationToken ct) =>
            GetJobAsync(id, db, ct)).WithName("GetJob");
        app.MapGet("/api/jobs/{jobId:guid}/pages/{pageId:guid}/links",
            (Guid jobId, Guid pageId, int? page, int? pageSize, CrawlerDbContext db, CancellationToken ct) =>
                GetPageLinksAsync(jobId, pageId, page, pageSize, db, ct)).WithName("GetPageLinks");
        app.MapPost("/api/jobs/{id:guid}/cancel", (Guid id, CrawlerDbContext db, CancellationToken ct) =>
            CancelJobAsync(id, db, ct)).WithName("CancelJob");
    }

    private static async Task<IResult> ListJobsAsync(
        int? page, int? pageSize, CrawlerDbContext db, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(pageSize ?? 20, 1, 50);
        var skip = (int)Math.Min(Math.Max((long)(page ?? 0), 0) * take, int.MaxValue);
        var jobs = await db.Jobs.AsNoTracking()
            .OrderByDescending(job => job.CreatedAt)
            .Skip(skip).Take(take)
            .Select(job => new
            {
                job.Id,
                job.RootUrl,
                job.MaxDepth,
                job.Status,
                job.CreatedAt,
                job.StartedAt,
                job.CompletedAt,
                job.FailureReason,
                PagesCrawled = db.Pages.Count(page => page.JobId == job.Id)
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(jobs);
    }

    private static async Task<IResult> CreateJobAsync(
        CreateJobRequest request, CrawlerDbContext db, CancellationToken cancellationToken)
    {
        if (!PublicUrlPolicy.TryParse(request.Url, out var uri, out var error))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["url"] = [error!] });
        if (request.Url.Length > 2048)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["url"] = ["URLs must be 2048 characters or fewer."] });
        if (request.MaxDepth is < 0 or > 3)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["maxDepth"] = ["Depth must be between 0 and 3."] });

        var job = new CrawlJob
        {
            Id = Guid.NewGuid(),
            RootUrl = PublicUrlPolicy.Normalize(uri!),
            MaxDepth = request.MaxDepth ?? 2
        };
        var outboxMessage = new OutboxMessage
        {
            Type = OutboxDispatcher.EventType,
            Payload = JsonSerializer.Serialize(new CrawlRequested(job.Id))
        };

        db.Jobs.Add(job);
        db.Outbox.Add(outboxMessage);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/jobs/{job.Id}", ApiModelMapper.ToSummary(job));
    }

    private static async Task<IResult> GetJobAsync(Guid id, CrawlerDbContext db, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking()
            .Include(item => item.Pages)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return job is null ? Results.NotFound() : Results.Ok(ApiModelMapper.ToSummary(job));
    }

    private static async Task<IResult> GetPageLinksAsync(
        Guid jobId, Guid pageId, int? page, int? pageSize, CrawlerDbContext db, CancellationToken cancellationToken)
    {
        var exists = await db.Pages.AsNoTracking().AnyAsync(item => item.Id == pageId && item.JobId == jobId, cancellationToken);
        if (!exists)
            return Results.NotFound();

        var take = Math.Clamp(pageSize ?? 50, 1, 100);
        var skip = (int)Math.Min(Math.Max((long)(page ?? 0), 0) * take, int.MaxValue);
        var links = await db.Links.AsNoTracking().Where(link => link.PageId == pageId)
            .OrderByDescending(link => link.IsInternal).ThenBy(link => link.TargetUrl)
            .Skip(skip).Take(take)
            .Select(link => new LinkSummary(link.TargetUrl, link.AnchorText, link.IsInternal))
            .ToListAsync(cancellationToken);
        return Results.Ok(links);
    }

    private static async Task<IResult> CancelJobAsync(Guid id, CrawlerDbContext db, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (job is null)
            return Results.NotFound();
        if (job.Status is CrawlJobStatus.Queued or CrawlJobStatus.Running)
        {
            job.Status = CrawlJobStatus.Canceled;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        return Results.Ok(new { job.Id, job.Status });
    }
}
