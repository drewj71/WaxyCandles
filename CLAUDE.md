# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

WaxyCandles is a full-stack stock alert and market-data platform: ASP.NET Core 9 / EF Core / SQL Server / RabbitMQ backend with background workers, plus a React/TypeScript frontend (`web/`). It's an active learning project focused on distributed-system patterns (outbox pattern, retry/dead-letter queues, idempotent consumers) alongside a trading/charting product.

## Commands

### Backend (.NET, run from repo root)

```bash
dotnet build WaxyCandles.sln          # build all projects
dotnet test                            # run tests (tests/WaxyCandles.Tests)
dotnet test --filter "FullyQualifiedName~ClassName.MethodName"  # run a single test
dotnet run --project src/WaxyCandles.Api
dotnet run --project src/WaxyCandles.PricePollingWorker
dotnet run --project src/WaxyCandles.AlertWorker
dotnet run --project src/WaxyCandles.OutboxWorker
docker compose up -d                   # start RabbitMQ only (management UI at localhost:15672)
```

SQL Server runs locally and is NOT part of the compose file.

For an end-to-end run, RabbitMQ plus the API and all three workers must be running (each is its own `dotnet run`).

EF Core migrations live in `src/WaxyCandles.Infrastructure/Migrations` and are committed to source control.

The test project (`tests/WaxyCandles.Tests`) currently has minimal coverage (xunit scaffold only) — do not assume behavior is pinned by existing tests. When writing integration tests, prefer real SQL Server and RabbitMQ (e.g. Testcontainers) over mocking the infrastructure.

### Frontend (from `web/`)

```bash
npm run dev       # vite dev server (expected at localhost:5173 — hardcoded as the API's CORS origin)
npm run build     # tsc -b && vite build
npm run lint      # eslint .
npm run preview
```

## Configuration & secrets

Secrets are NOT stored in appsettings files. In Development they come from `dotnet user-secrets` (each runnable project has its own UserSecretsId); in Docker/production they come from environment variables, where `:` becomes `__`.

| Key | Used by |
|---|---|
| `Jwt:Secret` | API only |
| `AlphaVantage:ApiKey` | PricePollingWorker |
| `ConnectionStrings:DefaultConnection` | API and all workers |

- `appsettings.json` holds structure and non-secret values only (e.g. RabbitMQ host `localhost`). Secret keys are present but blank.
- Never write a real secret into a tracked file, and never read, print, or log secret values.
- If a config value is missing at startup, tell me which key is missing and let me set it. Don't invent or hardcode a value.
- Workers need `DOTNET_ENVIRONMENT=Development` (set in `launchSettings.json`) or user secrets won't load.
- `web/.env` must only contain public values. Anything prefixed `VITE_` is compiled into the browser bundle, so never put secrets there.

## Architecture

### Backend: layered + message-driven

Projects under `src/` follow a clean/onion architecture:

- **WaxyCandles.Domain** — entities/enums, no dependencies on anything else.
- **WaxyCandles.Application** — interfaces, DTOs, and business operations, organized by feature (`Alerts/`, `Authentication/`, `MarketData/`, `Watchlists/`, `Notifications/`). Depends only on Domain.
- **WaxyCandles.Infrastructure** — implements Application's interfaces: EF Core persistence (`Persistence/`), Identity (`Identity/`), RabbitMQ messaging (`Messaging/`), market-data provider integrations (`MarketData/Providers/`). DI wiring lives in `DependencyInjection.cs` (`AddInfrastructure`, `AddApiAuthentication`).
- **WaxyCandles.Api** — ASP.NET Core Web API; thin controllers (`Controllers/`) that call into Application services. JWT bearer auth, Swagger/OpenAPI, CORS locked to the Vite dev origin.
- **WaxyCandles.PricePollingWorker / AlertWorker / OutboxWorker** — standalone background-service executables, each with their own `appsettings*.json`.

Market data access is abstracted behind `IMarketDataProvider` (Application layer) so vendors (currently Alpha Vantage) can be swapped without touching controllers or business logic.

### Async pipeline (Outbox pattern)

Price updates and alert evaluation are decoupled via RabbitMQ and the Outbox pattern:

```
Price Polling Worker -> writes StockPrice + OutboxMessage (same DB transaction)
  -> Outbox Worker reads unprocessed OutboxMessages -> publishes to RabbitMQ
    -> Alert Worker consumes "stock-price-updated" -> evaluates matching alerts
```

Failed messages go through a retry queue (`stock-price-retry`, 5s TTL) before landing in a dead-letter queue (`stock-price-dead-letter`) after repeated failures. The Alert Worker tracks `ProcessedMessage` IDs to guarantee idempotent (at-least-once-safe) consumption — check this when touching any consumer logic.

### Frontend: TanStack Router/Query, mid-migration

The app is mid-refactor from plain React Router to TanStack Router + TanStack Query + react-hook-form (see recent commit history). Current conventions:

- `web/src/routes/` contains **file-based route definitions only** — each file is a thin `createFileRoute(...)` wrapper that points `component` at a page from `web/src/pages/`. Route files should stay thin; actual page logic belongs in `pages/`.
- `web/src/routeTree.gen.ts` is **generated** by the `@tanstack/router-plugin` Vite plugin from `routes/` — never hand-edit it; it regenerates on dev/build.
- `web/src/api/` holds API client modules per resource (`alerts.ts`, `auth.ts`, `stocks.ts`, `watchlist.ts`), all going through the shared `client.ts` (axios instance, uses cookie-based auth rather than localStorage).
- `web/src/components/ui/` is shadcn-managed UI primitives (`components.json` config present); `components/charts/` holds chart-specific components (lightweight-charts / recharts).
- Path alias `@/` maps to `web/src/` (configured in `vite.config.ts` and the `tsconfig*.json` files).
- Auth uses httpOnly cookies set by the API, not localStorage, so the frontend doesn't need to manage tokens directly.

## Working rules

- After backend changes run `dotnet build`; after frontend changes run `npm run build` (it type-checks) and `npm run lint`.
- Changes to consumers, the outbox, or queue topology: explain the plan first and call out the effect on retry/dead-letter behavior and idempotency before editing.
- Never edit or delete applied EF migrations; add a new one.
- Don't hand-edit `web/src/routeTree.gen.ts`.
- Keep controllers thin; business logic goes in the Application layer.
