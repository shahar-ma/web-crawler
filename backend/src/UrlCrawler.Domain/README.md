# UrlCrawler.Domain

URLScout's dependency-light domain project defines crawl jobs, crawled pages/links, outbox messages, lifecycle statuses, and the `CrawlRequested` event. `PublicUrlPolicy` validates and normalizes HTTP(S) URLs, compares origins, and rejects loopback, private, reserved, and otherwise non-public IP destinations.

## Tests and coverage

Domain behavior is tested with xUnit table-driven URL and IP cases plus entity/link-ratio checks. Run from the backend directory with `dotnet test .\tests\UrlCrawler.Tests\UrlCrawler.Tests.csproj --collect:"XPlat Code Coverage"`. The latest full-suite run on 2026-11-09 passed all 76 tests; measured line/branch coverage was **99.0% / 94.4%** for `UrlCrawler.Domain` (API 94.0% / 96.7%, Infrastructure 96.6% / 91.7%, Worker 93.1% / 96.1%; overall 94.8% / 95.1%).

See the backend README for the complete coverage snapshot and remaining test priorities.
