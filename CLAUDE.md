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
docker compose -f docker-compose.app.yml up -d --build   # whole stack in containers (needs root .env)
```

For local development SQL Server runs on the host (Windows auth) and is NOT part of `docker-compose.yml`. `docker-compose.app.yml` includes it and adds its own containerised SQL Server (SQL auth, host port 14333), a one-shot EF migrations-bundle `migrator`, the API (host port 5148) and the three workers, all built from the root `Dockerfile` (`app` target with `--build-arg PROJECT=...`, or `migrator`). Required secrets come from a git-ignored root `.env` (`MSSQL_SA_PASSWORD`, `JWT_SECRET`, `ALPHAVANTAGE_API_KEY`); compose fails with "Set X in .env" if one is missing. `.dockerignore` excludes all `appsettings*.json`, so containers are configured only by environment variables.

CI (`.github/workflows/ci.yml`) runs backend build + tests, frontend lint + build, and a Docker image build on every PR and push to `master`.

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

Price updates, alert evaluation and notifications are decoupled via RabbitMQ and the Outbox pattern:

```
Price Polling Worker -> writes StockPrice + OutboxMessage (same DB transaction)
  -> Outbox Worker publishes each OutboxMessage to the queue for its Type (MessageRoutes)
    -> Alert Worker consumes "stock-price-updated" -> evaluates alerts; a triggered alert
       writes an AlertTriggeredMessage OutboxMessage in the same transaction
      -> Outbox Worker -> "alert-triggered"
        -> API (hosted AlertTriggeredConsumer) stores a Notification, pushes it over SignalR
```

- Each queue's topology (queue, `<name>-retry`, `-dlx`/`-dead-letter`) and its retry policy (`MaxRetries`, `RetryDelay`) are defined once in `QueueTopology` (Infrastructure/Messaging); producers and consumers both declare it with identical arguments. `stock-price-updated` retries 2x at 5s; `alert-triggered` 5x at 30s. `RetryDelay` is the retry queue's `x-message-ttl`, so changing it for a queue that already exists on a broker causes 406 PRECONDITION_FAILED until that queue is deleted. Add a new message type by adding a topology and a `MessageRoutes` entry. The outbox leaves rows of unrouted types pending.
- Consumers derive from `RabbitMqConsumer<TMessage>`, which owns transport concerns: reconnect loop, publisher-confirmed mandatory retry/dead-letter publishes, requeue safety net, unreadable payloads straight to the dead-letter queue. Subclasses implement `IsValid` and `ProcessAsync` and are responsible for idempotency.
- Idempotency: `StockPriceConsumer` claims a `ProcessedMessage` row inside the transaction that evaluates alerts, and `AlertEvaluationService` fires and re-arms alerts only through conditional updates that re-check, at write time, that the alert is unchanged since it was read (target/direction/frequency), still in the expected state, and that the price's observation time (`StockPriceUpdatedMessage.Timestamp`) is newer than the alert's `StateChangedAt` — so concurrent or out-of-order evaluations emit one event per fire, and an edit (which re-arms and stamps `StateChangedAt`) can't be overridden by a stale price; `AlertTriggeredConsumer` relies on the unique `Notification.SourceMessageId`, and its SignalR push has a 5s timeout because it runs before the ack. Check this when touching any consumer logic.
- The SignalR push (`/hubs/notifications`, cookie-authenticated) only reaches clients on the API instance that consumed the message; more than one API instance would need a SignalR backplane.

### Frontend: TanStack Router/Query, mid-migration

The app is mid-refactor from plain React Router to TanStack Router + TanStack Query + react-hook-form (see recent commit history). Current conventions:

- `web/src/routes/` contains **file-based route definitions only** — each file is a thin `createFileRoute(...)` wrapper that points `component` at a page from `web/src/pages/`. Route files should stay thin; actual page logic belongs in `pages/`.
- `web/src/routeTree.gen.ts` is **generated** by the `@tanstack/router-plugin` Vite plugin from `routes/` — never hand-edit it; it regenerates on dev/build.
- `web/src/api/` holds API client modules per resource (`alerts.ts`, `auth.ts`, `stocks.ts`, `watchlist.ts`), all going through the shared `client.ts` (axios instance, uses cookie-based auth rather than localStorage).
- `web/src/components/ui/` is shadcn-managed UI primitives (`components.json` config present); `components/charts/` holds chart-specific components (lightweight-charts / recharts).
- Path alias `@/` maps to `web/src/` (configured in `vite.config.ts` and the `tsconfig*.json` files).
- Auth uses httpOnly cookies set by the API, not localStorage, so the frontend doesn't need to manage tokens directly.

## Working rules

- `docs/PROGRESS.md` is the running log of what's changed and what's planned next. Read it at the start of a session for context; at the end of a session (or after finishing a meaningful chunk of work) add a dated log entry and update the "Next up" list.

- After backend changes run `dotnet build`; after frontend changes run `npm run build` (it type-checks) and `npm run lint`.
- Changes to consumers, the outbox, or queue topology: explain the plan first and call out the effect on retry/dead-letter behavior and idempotency before editing.
- Never edit or delete applied EF migrations; add a new one.
- Don't hand-edit `web/src/routeTree.gen.ts`.
- Keep controllers thin; business logic goes in the Application layer.
