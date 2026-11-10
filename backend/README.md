# URLScout backend

URLScout is a .NET 8 backend for submitting and tracking bounded website crawls. The REST API validates public HTTP/HTTPS URLs, creates crawl jobs, exposes job/page/link history, and supports cancellation. A worker consumes queued jobs, fetches same-origin pages, extracts titles and links, and stores crawl results.

## Architecture and infrastructure

- **API (`src/UrlCrawler.Api`)** — ASP.NET Core minimal APIs for job creation, listing, details, page links, cancellation, health checks, and Swagger/OpenAPI. Route handlers live in `ApiEndpoints`.
- **Domain (`src/UrlCrawler.Domain`)** — job, page, link, outbox, and request models, plus URL parsing, normalization, origin matching, and public-address policy.
- **Infrastructure (`src/UrlCrawler.Infrastructure`)** — EF Core SQL Server persistence and a transactional outbox dispatcher. The API commits each job and its outbox event together; the dispatcher publishes durable RabbitMQ messages and marks them published after broker confirms.
- **Worker (`src/UrlCrawler.Worker`)** — RabbitMQ consumer and crawl processor. Failed message processing is requeued; malformed messages are discarded. Crawls are limited to 25 pages, depth 0–3, 250 links per page, and 2 MiB per HTML response. Redirects are not followed; non-public destinations are rejected and only same-origin links are crawled.
- **Tests (`tests/UrlCrawler.Tests`)** — xUnit tests for domain rules, SQLite persistence mappings, API endpoints using EF InMemory, and worker/outbox behavior with mocked RabbitMQ and HTTP handlers.

SQL Server and RabbitMQ are required at runtime. Configure `ConnectionStrings__CrawlerDb` and `RabbitMq__Uri` for both API and worker. Use the PowerShell instructions in the repository's main README to run the full stack; the frontend is at <http://localhost:3000>, and API Swagger at <http://localhost:5080/swagger>.

## Build, test, and coverage

From this backend directory:

```powershell
dotnet build .\UrlCrawler.sln --configuration Release
dotnet test .\tests\UrlCrawler.Tests\UrlCrawler.Tests.csproj --configuration Release
dotnet test .\tests\UrlCrawler.Tests\UrlCrawler.Tests.csproj --collect:"XPlat Code Coverage"
```

The last command collects Coverlet line and branch coverage. The Cobertura report is written under `tests/UrlCrawler.Tests/TestResults`.

Latest full-suite snapshot measured 2026-11-09; all 76 tests passed. Overall line/branch coverage is **94.8% / 95.1%**:

| Project | Line coverage | Branch coverage |
|---|---:|---:|
| `UrlCrawler.Api` | 94.0% | 96.7% |
| `UrlCrawler.Domain` | 99.0% | 94.4% |
| `UrlCrawler.Infrastructure` | 96.6% | 91.7% |
| `UrlCrawler.Worker` | 93.1% | 96.1% |

`CrawlProcessor.cs` has **100% line and branch coverage**, including job claiming, resumed queues, persistence, completion, cancellation reset, and failure handling. SQLite tests exercise the relational `ExecuteUpdateAsync` paths using a SQLite-specific `StartedAt` conversion. Coverage is not enforced by the build. Remaining opportunities include:

- **API:** exercise database-initialization retry/exhaustion and the health-check exception path.
- **Infrastructure:** test cancellation during reconnect delay and recovery after publish/confirm failures.
- **Worker host:** cover startup/configuration and shutdown/reconnect paths without a live broker.
- **Domain:** add cases for any remaining uncovered IPv6 address-policy branches.

Inspect class/line details in the Cobertura report after each run; aggregate percentages alone can hide uncovered branches.

Inspect class/line details in the Cobertura report after each run; aggregate percentages alone can hide uncovered branches.
