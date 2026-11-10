# UrlCrawler.Worker

The .NET 8 hosted worker consumes durable `crawl-jobs` events from RabbitMQ, fetches bounded same-origin HTML pages, and persists page/link results. It uses `ICrawlProcessor` and the shared infrastructure project; malformed messages are discarded, processing failures are requeued, redirects are disabled, and destinations must be public. Limits are 25 pages per job, depth 0–3, 250 links per page, and 2 MiB per HTML response.

## Infrastructure and running

The worker depends on SQL Server for job/page/link state and RabbitMQ for job delivery. Run from the repository root with `dotnet run --project .\backend\src\UrlCrawler.Worker\UrlCrawler.Worker.csproj`. Development settings assume SQL Server on port 1433 with Windows integrated authentication and local RabbitMQ with the guest account. Override `ConnectionStrings__CrawlerDb` and `RabbitMq__Uri` as needed. For deployment, inject both values using environment variables or a secret store; do not place production credentials in settings files.

## Tests and coverage

Worker tests use mocked HTTP and RabbitMQ boundaries. Run them from the backend directory with `dotnet test .\tests\UrlCrawler.Tests\UrlCrawler.Tests.csproj --collect:"XPlat Code Coverage"`. The latest full-suite run on 2026-11-09 passed all 76 tests; measured line/branch coverage was **93.1% / 96.1%** for `UrlCrawler.Worker` (API 94.0% / 96.7%, Domain 99.0% / 94.4%, Infrastructure 96.6% / 91.7%; overall 94.8% / 95.1%).

`CrawlProcessor.cs` is fully covered at **100% line and branch coverage**, including claim eligibility/stale jobs, resume and queue expansion, page/depth handling, cancellation, completion, and persisted failures. Remaining Worker gaps include host startup/shutdown/reconnect paths; see the backend README for the full coverage table and broader priorities.
