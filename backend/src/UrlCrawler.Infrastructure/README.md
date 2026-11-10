# UrlCrawler.Infrastructure

Shared persistence and messaging infrastructure for URLScout. `CrawlerDbContext` maps jobs, pages, links, and outbox events to EF Core/SQL Server, including cascade relationships and query indexes. The API writes a job and outbox record together; the hosted dispatcher publishes durable RabbitMQ events and marks them published only after broker confirmation. RabbitMQ access is exposed through `IRabbitConnectionProvider` for testability.

## Tests and coverage

SQLite tests verify relational mappings and cascades; dispatcher tests use mocked RabbitMQ with EF InMemory. Run from the backend directory with `dotnet test .\tests\UrlCrawler.Tests\UrlCrawler.Tests.csproj --collect:"XPlat Code Coverage"`. The latest full-suite run on 2026-11-09 passed all 76 tests; measured line/branch coverage was **96.6% / 91.7%** for `UrlCrawler.Infrastructure` (API 94.0% / 96.7%, Domain 99.0% / 94.4%, Worker 93.1% / 96.1%; overall 94.8% / 95.1%).

Remaining dispatcher gaps are cancellation during the retry delay and recovery/error paths following publish or broker confirmation failures. Add controlled connection/channel failure tests to verify pending events remain unpublished for retry. See the backend README for coverage report location and Worker guidance.
