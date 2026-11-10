# UrlCrawler.Api

ASP.NET Core 8 minimal API for submitting URLScout crawl jobs, listing jobs, retrieving job/page/link details, and canceling active jobs. URL validation enforces HTTP(S) and public destinations. Job and outbox-event writes share one SQL Server save; a separate hosted dispatcher publishes durable RabbitMQ messages. The API exposes `/health` and Swagger in Development, with route handlers grouped in `ApiEndpoints`.

## Infrastructure and running

The API uses SQL Server through EF Core and RabbitMQ through the shared infrastructure project. Run from the repository root with `dotnet run --project .\backend\src\UrlCrawler.Api\UrlCrawler.Api.csproj --launch-profile http`. Development settings assume SQL Server on port 1433 with Windows integrated authentication and local RabbitMQ with the guest account. Override `ConnectionStrings__CrawlerDb` and `RabbitMq__Uri` as needed. For deployment, inject settings through environment variables or a secret store; do not put production credentials in settings files.

## Tests and coverage

API integration tests run the actual routes with an EF InMemory database; they do not require live SQL Server or RabbitMQ. Run from the backend directory with `dotnet test .\tests\UrlCrawler.Tests\UrlCrawler.Tests.csproj --collect:"XPlat Code Coverage"`. The latest full-suite run on 2026-11-09 passed all 76 tests; measured line/branch coverage was **94.0% / 96.7%** for `UrlCrawler.Api` (Domain 99.0% / 94.4%, Infrastructure 96.6% / 91.7%, Worker 93.1% / 96.1%; overall 94.8% / 95.1%).

Remaining API gaps include the database-initialization retry/exhaustion paths and the health-check exception handler. Test these by making startup dependencies replaceable or by hosting the app with controlled database failure behavior. See the backend README for the full coverage table and worker/infrastructure coverage guidance.
