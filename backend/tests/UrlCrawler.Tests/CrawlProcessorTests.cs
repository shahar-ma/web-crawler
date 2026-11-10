using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Reflection;
using UrlCrawler.Domain;
using UrlCrawler.Infrastructure;
using UrlCrawler.Worker;

namespace UrlCrawler.Tests;

public sealed class CrawlProcessorTests
{
    [Fact]
    public async Task CrawlPage_parses_title_links_and_computes_same_origin_link_counts()
    {
        await using var fixture = await Fixture.CreateAsync(_ => Response(HttpStatusCode.OK, "text/html",
            "<title> Home </title><a href='/next#top'> Next </a>" +
            "<a href='https://8.8.8.8:444/out'>Outside</a><a href='mailto:test@example.com'>Mail</a>"));

        var page = await fixture.CrawlAsync("https://8.8.8.8/", CancellationToken.None);

        Assert.Equal(CrawledPageStatus.Completed, page.Status);
        Assert.Equal("Home", page.Title);
        Assert.Equal(2, page.TotalLinks);
        Assert.Equal(1, page.InternalLinks);
        Assert.Contains(page.Links, link => link.TargetUrl == "https://8.8.8.8/next" && link.AnchorText == "Next" && link.IsInternal);
        Assert.Contains(page.Links, link => link.TargetUrl == "https://8.8.8.8:444/out" && !link.IsInternal);
        Assert.Equal("GET", fixture.LastMethod);
        Assert.Contains("UrlCrawlerDemo/1.0", fixture.LastUserAgent);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "text/html", CrawledPageStatus.Failed, "HTTP 404")]
    [InlineData(HttpStatusCode.OK, "application/json", CrawledPageStatus.Skipped, "The response is not HTML.")]
    public async Task CrawlPage_records_http_failures_and_non_html_responses(
        HttpStatusCode status, string contentType, CrawledPageStatus expected, string error)
    {
        await using var fixture = await Fixture.CreateAsync(_ => Response(status, contentType, "text"));

        var page = await fixture.CrawlAsync("https://8.8.8.8/", CancellationToken.None);

        Assert.Equal(expected, page.Status);
        Assert.Equal(error, page.Error);
    }

    [Fact]
    public async Task CrawlPage_rejects_declared_and_streamed_oversize_documents()
    {
        await using var declared = await Fixture.CreateAsync(_ =>
        {
            var response = Response(HttpStatusCode.OK, "text/html", "x");
            response.Content.Headers.ContentLength = 2 * 1024 * 1024 + 1;
            return response;
        });
        var declaredPage = await declared.CrawlAsync("https://8.8.8.8/", CancellationToken.None);
        Assert.Equal(CrawledPageStatus.Skipped, declaredPage.Status);
        Assert.Equal("The HTML document exceeds the 2 MB limit.", declaredPage.Error);

        await using var streamed = await Fixture.CreateAsync(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new OversizedContent() };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
            return response;
        });
        var streamedPage = await streamed.CrawlAsync("https://8.8.8.8/", CancellationToken.None);
        Assert.Equal(CrawledPageStatus.Skipped, streamedPage.Status);
        Assert.Equal("The HTML document exceeds the 2 MB limit.", streamedPage.Error);
    }

    [Fact]
    public async Task CrawlPage_limits_titles_links_and_anchor_text_and_discards_invalid_targets()
    {
        var links = string.Concat(Enumerable.Range(0, 251).Select(index => $"<a href='/page{index}'>{new string('x', 510)}</a>")) +
                    "<a href='javascript:alert(1)'>script</a><a href='" + new string('a', 2050) + "'>long</a>";
        await using var fixture = await Fixture.CreateAsync(_ => Response(HttpStatusCode.OK, "text/html",
            $"<title>{new string('t', 510)}</title>{links}"));

        var page = await fixture.CrawlAsync("https://8.8.8.8/", CancellationToken.None);

        Assert.Equal(500, page.Title!.Length);
        Assert.Equal(250, page.TotalLinks);
        Assert.All(page.Links, link => Assert.Equal(500, link.AnchorText!.Length));
        Assert.DoesNotContain(page.Links, link => link.TargetUrl.Contains("javascript", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CrawlPage_records_host_and_transport_errors_but_propagates_requested_cancellation()
    {
        await using var nonPublic = await Fixture.CreateAsync(_ => throw new InvalidOperationException("must not send"));
        var rejected = await nonPublic.CrawlAsync("http://192.168.1.1/", CancellationToken.None);
        Assert.Equal(CrawledPageStatus.Failed, rejected.Status);
        Assert.Equal("The URL resolves to a non-public address.", rejected.Error);

        await using var networkFailure = await Fixture.CreateAsync(_ => throw new HttpRequestException("network error"));
        var failed = await networkFailure.CrawlAsync("https://8.8.8.8/", CancellationToken.None);
        Assert.Equal(CrawledPageStatus.Failed, failed.Status);
        Assert.Equal("network error", failed.Error);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nonPublic.CrawlAsync("https://8.8.8.8/", cancellation.Token));
    }

    [Fact]
    public async Task CrawlPage_discards_invalid_targets_and_clears_blank_anchor_text()
    {
        var longPath = new string('a', 2050);
        await using var fixture = await Fixture.CreateAsync(_ => Response(HttpStatusCode.OK, "text/html",
            "<a href='http://['>invalid</a><a href='ftp://example.com/file'>ftp</a>" +
            $"<a href='/{longPath}'>long</a><a href='/blank'>   </a>" +
            "<a href='http://8.8.8.8/alternate'>http</a>"));

        var page = await fixture.CrawlAsync("https://8.8.8.8/", CancellationToken.None);

        Assert.Equal(CrawledPageStatus.Completed, page.Status);
        Assert.Equal(2, page.Links.Count);
        Assert.Contains(page.Links, link => link.TargetUrl == "https://8.8.8.8/blank" && link.AnchorText is null);
        Assert.Contains(page.Links, link => link.TargetUrl == "http://8.8.8.8/alternate" && !link.IsInternal);
    }

    [Fact]
    public void CreateLink_allows_missing_anchor_text()
    {
        var method = typeof(CrawlProcessor).GetMethod("CreateLink", BindingFlags.Static | BindingFlags.NonPublic)!;
        var link = (CrawledLink?)method.Invoke(null,
            ["/next", null, new Uri("https://8.8.8.8/"), new Uri("https://8.8.8.8/")]);

        Assert.NotNull(link);
        Assert.Null(link.AnchorText);
    }

    [Fact]
    public async Task CrawlPage_skips_html_response_without_a_content_type()
    {
        await using var fixture = await Fixture.CreateAsync(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<title>no type</title>") };
            response.Content.Headers.ContentType = null;
            return response;
        });

        var page = await fixture.CrawlAsync("https://8.8.8.8/", CancellationToken.None);

        Assert.Equal(CrawledPageStatus.Skipped, page.Status);
        Assert.Equal("The response is not HTML.", page.Error);
    }

    [Theory]
    [InlineData("task-canceled")]
    [InlineData("socket")]
    public async Task CrawlPage_records_transport_exceptions(string exceptionKind)
    {
        await using var fixture = await Fixture.CreateAsync(_ => exceptionKind switch
        {
            "task-canceled" => throw new TaskCanceledException("request timed out"),
            "socket" => throw new System.Net.Sockets.SocketException(),
            _ => throw new InvalidOperationException("unexpected test input")
        });

        var page = await fixture.CrawlAsync("https://8.8.8.8/", CancellationToken.None);

        Assert.Equal(CrawledPageStatus.Failed, page.Status);
        Assert.False(string.IsNullOrWhiteSpace(page.Error));
    }

    [Fact]
    public async Task FailClaimedJobAsync_truncates_long_failure_messages()
    {
        await using var fixture = await Fixture.CreateAsync(_ => throw new InvalidOperationException("must not send"));
        var job = new CrawlJob { RootUrl = fixture.RootUrl.AbsoluteUri, MaxDepth = 0, Status = CrawlJobStatus.Running };
        await fixture.AddJobAsync(job);
        var method = typeof(CrawlProcessor).GetMethod("FailClaimedJobAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(fixture.Processor,
            [job.Id, new InvalidOperationException(new string('x', 600)), CancellationToken.None])!;

        await task;

        var savedJob = await fixture.GetJobAsync(job.Id);
        Assert.Equal(CrawlJobStatus.Failed, savedJob.Status);
        Assert.Equal(500, savedJob.FailureReason!.Length);
        Assert.NotNull(savedJob.CompletedAt);
    }

    [Fact]
    public async Task ProcessAsync_claims_queued_job_and_crawls_internal_pages_once()
    {
        await using var fixture = await Fixture.CreateAsync(request => request.RequestUri!.AbsolutePath == "/"
            ? Response(HttpStatusCode.OK, "text/html",
                "<a href='/next'>next</a><a href='/next#fragment'>duplicate</a><a href='/'>self</a>")
            : Response(HttpStatusCode.OK, "text/html", "<title>Next</title>"));
        var job = new CrawlJob { RootUrl = fixture.RootUrl.AbsoluteUri, MaxDepth = 1, FailureReason = "old failure" };
        await fixture.AddJobAsync(job);

        await fixture.ProcessAsync(job.Id, CancellationToken.None);

        var savedJob = await fixture.GetJobAsync(job.Id);
        var pages = await fixture.GetPagesAsync(job.Id);
        Assert.Equal(CrawlJobStatus.Completed, savedJob.Status);
        Assert.Null(savedJob.FailureReason);
        Assert.NotNull(savedJob.StartedAt);
        Assert.NotNull(savedJob.CompletedAt);
        Assert.Equal(2, pages.Count);
        Assert.Contains(pages, page => page.Url == fixture.RootUrl.AbsoluteUri && page.Depth == 0);
        Assert.Contains(pages, page => page.Url == "https://8.8.8.8/next" && page.Depth == 1);
    }

    [Fact]
    public async Task ProcessAsync_ignores_active_jobs_and_claims_stale_running_jobs()
    {
        await using var fixture = await Fixture.CreateAsync(_ => Response(HttpStatusCode.OK, "text/html", ""));
        var active = new CrawlJob
        {
            RootUrl = fixture.RootUrl.AbsoluteUri,
            MaxDepth = 0,
            Status = CrawlJobStatus.Running,
            StartedAt = DateTimeOffset.UtcNow
        };
        var stale = new CrawlJob
        {
            RootUrl = fixture.RootUrl.AbsoluteUri,
            MaxDepth = 0,
            Status = CrawlJobStatus.Running,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-11)
        };
        await fixture.AddJobAsync(active);
        await fixture.AddJobAsync(stale);

        await fixture.ProcessAsync(active.Id, CancellationToken.None);
        await fixture.ProcessAsync(stale.Id, CancellationToken.None);

        Assert.Equal(CrawlJobStatus.Running, (await fixture.GetJobAsync(active.Id)).Status);
        Assert.Empty(await fixture.GetPagesAsync(active.Id));
        Assert.Equal(CrawlJobStatus.Completed, (await fixture.GetJobAsync(stale.Id)).Status);
        Assert.Single(await fixture.GetPagesAsync(stale.Id));
    }

    [Fact]
    public async Task ProcessAsync_resumes_only_completed_pages_below_max_depth()
    {
        await using var fixture = await Fixture.CreateAsync(_ => Response(HttpStatusCode.OK, "text/html", ""));
        var job = new CrawlJob { RootUrl = fixture.RootUrl.AbsoluteUri, MaxDepth = 1 };
        var completed = new CrawledPage
        {
            JobId = job.Id, Url = fixture.RootUrl.AbsoluteUri, UrlKey = "root", Depth = 0,
            Status = CrawledPageStatus.Completed
        };
        completed.Links.Add(new CrawledLink { TargetUrl = "https://8.8.8.8/resume", IsInternal = true });
        completed.Links.Add(new CrawledLink { TargetUrl = "https://8.8.8.8/resume#fragment", IsInternal = true });
        completed.Links.Add(new CrawledLink { TargetUrl = "not an absolute url", IsInternal = true });
        completed.Links.Add(new CrawledLink { TargetUrl = "https://8.8.8.8/ignored", IsInternal = false });
        var failed = new CrawledPage
        {
            JobId = job.Id, Url = "https://8.8.8.8/failed", UrlKey = "failed", Depth = 0,
            Status = CrawledPageStatus.Failed
        };
        failed.Links.Add(new CrawledLink { TargetUrl = "https://8.8.8.8/ignored-failed", IsInternal = true });
        var atLimit = new CrawledPage
        {
            JobId = job.Id, Url = "https://8.8.8.8/at-limit", UrlKey = "limit", Depth = 1,
            Status = CrawledPageStatus.Completed
        };
        atLimit.Links.Add(new CrawledLink { TargetUrl = "https://8.8.8.8/ignored-depth", IsInternal = true });
        job.Pages.Add(completed);
        job.Pages.Add(failed);
        job.Pages.Add(atLimit);
        await fixture.AddJobAsync(job);

        await fixture.ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(CrawlJobStatus.Completed, (await fixture.GetJobAsync(job.Id)).Status);
        var pages = await fixture.GetPagesAsync(job.Id);
        Assert.Equal(4, pages.Count);
        Assert.Contains(pages, page => page.Url == "https://8.8.8.8/resume" && page.Depth == 1);
        Assert.DoesNotContain(pages, page => page.Url.Contains("ignored", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessAsync_does_not_complete_if_job_stops_during_crawl()
    {
        await using var fixture = await Fixture.CreateAsync(_ => Response(HttpStatusCode.OK, "text/html", "<a href='/next'>next</a>"));
        var job = new CrawlJob { RootUrl = fixture.RootUrl.AbsoluteUri, MaxDepth = 1 };
        await fixture.AddJobAsync(job);
        fixture.AfterResponse = () =>
        {
            fixture.AfterResponse = null;
            fixture.SetJobStatus(job.Id, CrawlJobStatus.Canceled);
        };

        await fixture.ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(CrawlJobStatus.Canceled, (await fixture.GetJobAsync(job.Id)).Status);
        Assert.Single(await fixture.GetPagesAsync(job.Id));
    }

    [Fact]
    public async Task ProcessAsync_resets_claim_when_cancellation_is_requested()
    {
        using var cancellation = new CancellationTokenSource();
        await using var fixture = await Fixture.CreateAsync(_ => Response(HttpStatusCode.OK, "text/html", ""));
        var job = new CrawlJob { RootUrl = fixture.RootUrl.AbsoluteUri, MaxDepth = 0 };
        await fixture.AddJobAsync(job);
        fixture.AfterResponse = () => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ProcessAsync(job.Id, cancellation.Token));

        var savedJob = await fixture.GetJobAsync(job.Id);
        Assert.Equal(CrawlJobStatus.Queued, savedJob.Status);
        Assert.Null(savedJob.StartedAt);
    }

    [Fact]
    public async Task ProcessAsync_marks_job_failed_when_saved_crawl_state_is_invalid()
    {
        await using var fixture = await Fixture.CreateAsync(_ => throw new InvalidOperationException("must not send"));
        var job = new CrawlJob { RootUrl = fixture.RootUrl.AbsoluteUri, MaxDepth = 0 };
        job.Pages.Add(new CrawledPage
        {
            JobId = job.Id, Url = "not a uri", UrlKey = "invalid", Status = CrawledPageStatus.Completed
        });
        await fixture.AddJobAsync(job);

        await fixture.ProcessAsync(job.Id, CancellationToken.None);

        var savedJob = await fixture.GetJobAsync(job.Id);
        Assert.Equal(CrawlJobStatus.Failed, savedJob.Status);
        Assert.NotNull(savedJob.CompletedAt);
        Assert.Contains("URI", savedJob.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CrawlJobStatus.Completed)]
    [InlineData(CrawlJobStatus.Failed)]
    [InlineData(CrawlJobStatus.Canceled)]
    public async Task ProcessAsync_does_not_claim_terminal_jobs(CrawlJobStatus status)
    {
        await using var fixture = await Fixture.CreateAsync(_ => throw new InvalidOperationException("must not send"));
        var job = new CrawlJob { RootUrl = fixture.RootUrl.AbsoluteUri, MaxDepth = 0, Status = status };
        await fixture.AddJobAsync(job);

        await fixture.ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(status, (await fixture.GetJobAsync(job.Id)).Status);
        Assert.Empty(await fixture.GetPagesAsync(job.Id));
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string contentType, string content)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(content) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return response;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly HttpClient _client;
        private readonly CrawlProcessor _processor;
        private readonly CrawlerDbContext _db;
        private readonly Uri _root = new("https://8.8.8.8/");
        public Uri RootUrl => _root;
        public CrawlProcessor Processor => _processor;
        public string LastMethod { get; private set; } = "";
        public string LastUserAgent { get; private set; } = "";
        public Action? AfterResponse { get; set; }

        private Fixture(SqliteConnection connection, HttpClient client)
        {
            _connection = connection;
            _client = client;
            _db = new CrawlerDbContext(new DbContextOptionsBuilder<CrawlerDbContext>().UseSqlite(connection).Options);
            _processor = new CrawlProcessor(_db, client, NullLogger<CrawlProcessor>.Instance);
        }

        public static async Task<Fixture> CreateAsync(Func<HttpRequestMessage, HttpResponseMessage> send)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            Fixture? fixture = null;
            var client = new HttpClient(new Handler((request, token) =>
            {
                token.ThrowIfCancellationRequested();
                fixture!.LastMethod = request.Method.Method;
                fixture.LastUserAgent = string.Join(" ", request.Headers.UserAgent);
                var response = send(request);
                fixture.AfterResponse?.Invoke();
                return response;
            }));
            fixture = new Fixture(connection, client);
            await fixture._db.Database.EnsureCreatedAsync();
            return fixture;
        }

        public async Task AddJobAsync(CrawlJob job)
        {
            _db.Jobs.Add(job);
            await _db.SaveChangesAsync();
        }

        public Task<CrawlJob> GetJobAsync(Guid id) => _db.Jobs.AsNoTracking().SingleAsync(job => job.Id == id);

        public Task<List<CrawledPage>> GetPagesAsync(Guid jobId) =>
            _db.Pages.AsNoTracking().Where(page => page.JobId == jobId).ToListAsync();

        public void SetJobStatus(Guid id, CrawlJobStatus status) =>
            _db.Jobs.Where(job => job.Id == id).ExecuteUpdate(updates => updates.SetProperty(job => job.Status, status));

        public Task ProcessAsync(Guid jobId, CancellationToken cancellationToken = default) =>
            _processor.ProcessAsync(new CrawlRequested(jobId), cancellationToken);

        public async Task<CrawledPage> CrawlAsync(string value, CancellationToken cancellationToken = default)
        {
            var method = typeof(CrawlProcessor).GetMethod("CrawlPageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var task = (Task<CrawledPage>)method.Invoke(_processor,
                [Guid.NewGuid(), _root, new Uri(value), 0, cancellationToken])!;
            return await task;
        }

        public async ValueTask DisposeAsync()
        {
            await _db.DisposeAsync();
            _client.Dispose();
            await _connection.DisposeAsync();
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request, cancellationToken));
    }

    private sealed class OversizedContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(new byte[2 * 1024 * 1024 + 1], 0, 2 * 1024 * 1024 + 1);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
