# UrlCrawler.Tests

This xUnit project tests URLScout's .NET 8 API, domain, persistence infrastructure, outbox dispatcher, and crawl worker. API tests host the real endpoints with an EF InMemory database; infrastructure mapping tests use SQLite; worker and outbox tests use mocked HTTP/RabbitMQ boundaries. No SQL Server or RabbitMQ service is required for the unit test suite.

From the backend directory, run:

```powershell
dotnet test .\tests\UrlCrawler.Tests\UrlCrawler.Tests.csproj
dotnet test .\tests\UrlCrawler.Tests\UrlCrawler.Tests.csproj --collect:"XPlat Code Coverage"
```

Coverlet writes a Cobertura report under `TestResults`. Latest full-suite snapshot measured 2026-11-09 (76 passing tests): overall line/branch coverage is **94.8% / 95.1%**.

| Production project | Line | Branch |
|---|---:|---:|
| `UrlCrawler.Api` | 94.0% | 96.7% |
| `UrlCrawler.Domain` | 99.0% | 94.4% |
| `UrlCrawler.Infrastructure` | 96.6% | 91.7% |
| `UrlCrawler.Worker` | 93.1% | 96.1% |

`CrawlProcessor.cs` has **100% line and branch coverage**, including job claim/resume, queue expansion, page saving, completion, cancellation reset, and failure updates. SQLite tests run these `ExecuteUpdateAsync` paths with a SQLite-specific timestamp conversion. Remaining opportunities include API database-initialization retries/health-check exceptions, infrastructure outbox reconnect/error paths, and worker-host startup/shutdown. Coverage percentages are informational; there is no enforced 100% threshold yet.
