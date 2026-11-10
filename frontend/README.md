# URLScout frontend

Responsive React + TypeScript dashboard built with Vite. It submits and monitors URLScout crawl jobs, displays discovered pages and domain-link ratios, and fetches page links on demand. The UI communicates with the ASP.NET Core API; crawl execution and persistence are handled by the backend worker, SQL Server, and RabbitMQ.

## Run locally (PowerShell)

```powershell
npm.cmd ci
npm.cmd run dev
```

The Vite development server proxies `/api` and `/health` to `http://localhost:5080`; run the API and worker with SQL Server and RabbitMQ. To launch the full stack, use `docker compose up --build` from the repository root.

## Verify and coverage status

```powershell
npm.cmd run lint
npm.cmd run build
```

The frontend currently has lint and build checks but no unit-test or coverage script, so a frontend coverage percentage is not available. The latest backend run (76 tests, measured 2026-11-09) reports **94.8% line / 95.1% branch** coverage overall: API **94.0% / 96.7%**, Domain **99.0% / 94.4%**, Infrastructure **96.6% / 91.7%**, and Worker **93.1% / 96.1%**. `CrawlProcessor.cs` has **100% line and branch coverage**. See `../backend/README.md` for the full backend coverage snapshot.
