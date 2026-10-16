# Progress log

Running record of what's changed and what's planned next. Newest entries first.
Update this at the end of each working session.

## Next up

- [ ] **Live-test the polling worker** (merged to `master` 2026-10-15, not yet run live):
  during market hours (prices stored with quote time, one batched call per poll) and after the
  close (logs "Market closed. Next price poll at ...", no calendar warnings, so the trial
  allows `markets/status`). Market Data trial ends ~2026-11-13.
- [ ] **Live-test GEX level alerts** (`feat/gex-level-alerts`): create one from a GEX level card,
  check the next morning's refresh log ("Alert ...: SPY King is at ...; target now ..."), and that a
  notification reads "SPY rose above $X, approaching its King node at $Y".
- [ ] **Look at a real trace**: `docker compose up -d`, run the API and workers, open
  http://localhost:18888 and follow a price from poll to notification (`feat/distributed-tracing`).
- [ ] **GEX follow-ups**: % of King display, node velocity (needs stored snapshots), a GEX panel on
  the stock page.
- [ ] **Schwab market data provider**: developer account applied for, access **pending** (as of 2026-10-14).
  Lower priority now that MarketData.app `stocks/prices` is real-time.
  Open questions:
  - OAuth 2.0 with browser login: ~30 min access tokens, ~7 day refresh tokens, so weekly manual
    re-auth. Need a token store and a re-auth story for the headless `PricePollingWorker`.
  - Schwab streaming (WebSocket) vs. keeping the current poll → outbox pipeline.

## Log

### 2026-10-16
- `feat/distributed-tracing`: OpenTelemetry traces and logs from the API and all workers to a local
  .NET Aspire dashboard (`docker compose up -d`, http://localhost:18888). Trace context crosses the
  outbox (new nullable `OutboxMessage.TraceParent`/`TraceState`, migration `AddOutboxTraceContext`)
  and RabbitMQ (`traceparent` headers, copied onto retry and dead-letter publishes), so a price is one
  trace from poll to SignalR push. On only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (it is in
  launchSettings). No change to routing, retry counts/delays, topology or idempotency.
- messaging-reviewer: no significant findings. Deploy order matters: apply `AddOutboxTraceContext`
  before running the new code (EF now names the columns, so against an old schema the alert worker
  would dead-letter alert-firing prices and the poller/outbox would fail), and roll back code before
  schema. Docker's migrator enforces this; with `dotnet run`, run `dotnet ef database update` first.

### 2026-10-15
- Tested MarketData.app end to end locally: AAPL history import worked and a polled
  quote triggered an alert notification.
- Docs check: `stocks/quotes` is not real-time (trial/free get the last closed session, paid
  plans 15-min delayed without exchange agreements), so that alert likely fired on the previous
  close. Switched quotes to `stocks/prices`, real-time SmartMid (bid/ask midpoint) on every
  plan, same 1 credit per symbol, no volume.
- Confirmed Market Data has no WebSocket/streaming API (REST only).
- `feat/realtime-polling`: price polling every 1 min (configurable) during market hours only,
  all symbols in one batched `stocks/prices` call, prices stamped with the provider's quote time
  instead of poll time (fixes stale quotes passing as fresh in alert ordering), and unchanged
  quotes no longer re-stored/re-published.
- Holidays: `IMarketCalendar` backed by Market Data's `markets/status` (one request per 30 days,
  cached a day). The worker skips closed days and sleeps to the next trading day's open. Falls
  back to "weekdays trade" on any failure (retry after 15 min). Half days count as open.
  Full suite: 286 tests pass.
- `feat/marketdata-app-provider` and `feat/realtime-polling` merged to `master`.
- `feat/gex-heatmap`: GEX/VEX heatmap (Skylit Heatseeker style) at `/gex?symbol=`. Strikes ×
  expirations, viridis colouring, King/floor/ceiling/gatekeeper marks, 1W–6M expiries and ±5–30%
  strikes. Backend: `IOptionsDataProvider` (Market Data `options/chain`, windowed by strike and
  expiration; optional `MarketDataApp:OptionsMode`), `GexCalculator` with a Black-Scholes fallback
  for missing gamma/IV, `GET /api/stocks/{symbol}/gex` cached a minute. Naive dealer model
  (calls +, puts -). Trial plans get the previous session's chain; the page says so.
  Tested locally against the real API: works.
- `feat/gex-level-alerts`: alerts on GEX levels (King/Gatekeeper/Floor/Ceiling, within 0-5%). A level
  alert is a price alert whose target/direction are derived from the level ("approaching" from the
  price's side) and re-resolved once per trading day by the PricePollingWorker before the first poll.
  New `Alerts` columns (additive migration `AddGexLevelAlerts`); `AlertTriggeredMessage` gains nullable
  `Level`/`LevelPrice`. No consumer, topology, retry or outbox changes. Alerts page form and list, and
  a bell on each GEX level card that opens a pre-filled alert.
- messaging-reviewer: guarantees hold (no duplicate events, compatible message change). Fixed its
  findings: a retry/restart mid-session could re-resolve against the current price, flip direction and
  fire one approach twice (now at most once per trading date, persisted as `LevelCheckedOn`, and
  retries only reload failed boards); refresh stamped wall-clock time, which silenced lagging quotes
  (now the newest stored price time, never moved back); a revived alert kept a stale target (any edit
  to an alert not checked today re-resolves it, without blocking the edit if the level is gone).

### 2026-10-14
- Started this progress log. Context from earlier sessions wasn't saved, so the history
  below is reconstructed from git.
- `fix/alert-lifecycle-races` merged to `master`.
- Evaluated MarketData.app: good for candles and options (chains, IV, Greeks). Caveats: API
  stock quotes are 15-min delayed on self-serve plans, internal-use licence only, one IP per
  account, option chains cost 1 credit per contract.
- Added `MarketDataAppMarketDataProvider` and config-driven provider selection
  (`MarketData:Provider` = `MarketDataApp` | `AlphaVantage`, unset → Alpha Vantage)
  on `feat/marketdata-app-provider`. Token in `MarketDataApp:ApiToken` (Bearer header);
  Docker uses `MARKETDATA_API_TOKEN`. Unit tests for parsing, errors and provider selection.

### 2026-10-13
- Closed races between alert evaluation, alert edits and out-of-order prices
  (`fix/alert-lifecycle-races`, not yet merged).

### 2026-10-12
- Alert lifecycle: enable/disable, repeating alerts, expiry (backend + Alerts page controls).

### 2026-10-11
- Alert-triggered notifications: outbox → `alert-triggered` queue → API consumer → SignalR,
  plus the notification bell and live toasts in the web app. Fixed messaging-reviewer findings.
- GitHub Actions CI and a full-stack Docker Compose setup.
- Prices/percentages stored as `decimal(18,6)` instead of `(18,2)`.
- Alpha Vantage timestamps parsed independently of the server time zone.
- Frontend build and lint fixed.

### 2026-10-10
- Test suite added in four priority tiers (Testcontainers for SQL Server/RabbitMQ); fixed the
  seven bugs it found.
- Hardened the consumer and outbox against broker-side failures.
- Moved secrets to user secrets.

### 2026-10-05
- Frontend started; migrated to TanStack Router/Query and react-hook-form.
- Switched auth to httpOnly cookies.
