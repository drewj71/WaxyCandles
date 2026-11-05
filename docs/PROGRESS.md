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
- [ ] **Watch Market Data credits per day** before the trial ends ~2026-11-13: sum
  `waxycandles.marketdata.credits` per day (plans reset 9:30 ET) in the Aspire dashboard, by endpoint.
  This is the input for the plan decision.
- [ ] **Deploy Phase 1**: apply migration `OutboxIndexesAndRetention` while the market is closed (index
  builds on the never-pruned outbox table block the publisher and poller). Expect the first
  retention pass to purge the backlog in paused batches.
- [ ] **Deploy Phase 2**: migration `HashRefreshTokens` (hashes existing tokens in place, so nobody
  logs in again; rolling it back deletes all refresh tokens). Check the worker containers report
  healthy in `docker compose ps`.
- [ ] **Deploy the hardening batch**: migration `RefreshTokenFamilies` (quick; two indexes on
  `RefreshTokens`). Reuse detection covers only rotations after it.
- [ ] **Deploy the features batch**: migration `NamedWatchlists` (rewrites `Watchlists`; quick on a
  small table). Then try the web app live: watchlist tabs and live ticks during market hours, a
  backtest on SPY (imports history once, about 5 credits for 20 years).
- [ ] **Features follow-ups**:
  - Count only real imports against the backtest rate limit, not every backtest.
  - Remember "no data before X" per symbol, so a symbol with shorter history doesn't re-import
    (about 1 credit) on every backtest.
  - The history-import endpoint can store today's partial candle during market hours.
  - Stock page "Add to watchlist" star does nothing yet; watchlist limits can be exceeded by one
    under concurrent adds.
  - Next: parameter sweeps (heatmap), saved backtests, shorting, a rule builder.
- [ ] **Phase 2 and hardening follow-ups**:
  - An API container health check (the aspnet image has no curl) or an orchestrator HTTP probe.
  - Share the RabbitMQ connection-factory settings between `RabbitMqConnection` and the broker
    health check.
  - Ambiguous-commit replays still give a 500 for watchlist remove and for Identity writes (rare).
  - `marketdata.credits` misses timed-out requests; a vendor 429 is `outcome=error` (add
    `rate_limited`).
  - Refresh sessions have no maximum age (each rotation slides expiry 30 days); a reuse-detected
    metric; the grace window and lifetime are code constants.
  - `GexLevelAlertRefresher` falls back to the wall clock for a symbol with no stored price, which
    can mute a delayed feed's first quotes (create now leaves the stamp null instead).
  - A poll committing between the stamp read and an alert's save still counts as after the alert.
  - Compose only marks a stalled container unhealthy; restarting it needs autoheal or an orchestrator.
  - Alpha Vantage rate-limit detection follows its docs and is untested against the real API.
- [ ] **Phase 1 follow-ups**:
  - Retention metrics (rows deleted per table) now that the Meter exists.
  - An index on `StockPrices.Timestamp` if the retention delete shows up as slow.
  - Make `Prefetch_limit_lets_a_second_consumer_share_a_backlog` deterministic: it depends on
    timing and could flake on a slow CI agent.
  - Any future dead-letter replay tool must stay inside the `ProcessedMessages` window (7 days).
- [ ] **Decisions needed from Phase 0**:
  - `POST /api/stocks/{symbol}/historical` isn't called by the web app: keep it (now 5/min per user),
    make it admin-only, or remove it?
  - Forwarded headers and a trusted-proxy list before deploying behind a proxy, or the per-IP auth
    limit is shared by every client.
- [ ] **Phase 0 follow-ups**:
  - `RabbitMqConnection` connects synchronously and ignores cancellation; an async, cancellable
    connect would fix slow shutdown when the broker host doesn't answer.
- [ ] **IV analytics on the GEX page**: ATM IV term structure and skew per expiration, from the
  chain already fetched.
- [ ] **Housekeeping (needs you)**: delete the 11 merged branches (my attempt was blocked by the
  permission check), and add `MARKETDATA_API_TOKEN=` and the optional `CORS_ALLOWED_ORIGIN=` to
  `.env.example` (I can't edit `.env*`).
- [ ] **GEX follow-ups**: % of King display, node velocity (needs stored snapshots), a GEX panel on
  the stock page.
- [ ] **Schwab market data provider**: developer account applied for, access **pending** (as of 2026-10-14).
  Lower priority once on a paid Market Data plan, where `stocks/prices` is real-time (not on the
  trial; see 2026-10-28).
  Open questions:
  - OAuth 2.0 with browser login: ~30 min access tokens, ~7 day refresh tokens, so weekly manual
    re-auth. Need a token store and a re-auth story for the headless `PricePollingWorker`.
  - Schwab streaming (WebSocket) vs. keeping the current poll → outbox pipeline.

## Log

### 2026-11-04 (features: live prices, watchlists, backtesting)
- Two parallel agents, combined on `integration/features`.
- Live prices and watchlists (`feat/live-prices-watchlists`):
  - `PricesHub` (`/hubs/prices`) plus `LivePriceBroadcaster`: new stored prices pushed to subscribed
    browsers every 2 s, read from `StockPrices` only (no queue or vendor changes). Stock page price,
    change and forming candle tick live; "Live / Market closed" indicator.
  - Named watchlists (migration `NamedWatchlists`, hand-written, moves rows into "My Watchlist"),
    `/api/watchlists` CRUD and reorder, sortable quote table with 30-day sparklines. The old
    `/api/watchlist` endpoints act on the first list. Poller symbol set unchanged (pinned by a test).
  - Stock details now return the real `PreviousClose` and `QuotedAt` (previous close showed $0).
- Backtesting (`feat/backtesting`): pure engine (next-open fills, long-only, costs), MA crossover,
  RSI mean reversion, Donchian breakout and buy-and-hold benchmark; `POST /api/backtests` imports a
  missing range once (shares the `history-import` limit); `/backtest` page with metrics, equity curve,
  trade markers and a trades table. History imports now retry on duplicate-key races.
- Combined: one trivial chart conflict; full suite 749 passed, 0 failed (15 m 48 s); web build and
  lint pass.

### 2026-11-04 (hardening batch)
- Follow-ups from Phases 0–2, three parallel agents, combined on `integration/hardening`.
- `fix/alert-stamp-fired-heartbeats` (plan approved first; touches the AlertWorker consumer):
  - Alert create stamps `StateChangedAt` with the newest stored price time (provider clock), so a
    price observed before the alert existed can't fire it. Edits use the same stamp, which also
    stops a delayed feed muting an edited alert.
  - `alerts.fired` counts committed fires only, recorded after the AlertWorker's commit.
  - Explicit `OutboxLoopHeartbeat`; AlertWorker stall detection (a delivery in `ProcessAsync` over
    5 min). No change to ack, retry or dead-letter behaviour.
- `feat/vendor-error-mapping`: vendor timeout → 504, vendor rate limit → 503 with `Retry-After`,
  client disconnect → 499, through a typed `MarketDataUnavailableException`. The web app shows
  429/503/504 messages (login used to say "Invalid email or password" on a 429) and never retries
  them.
- `feat/refresh-token-families`: token families with reuse detection (a replayed rotated token
  after a 10 s grace window revokes the family), one winner for concurrent refreshes, logout ends
  the session, `ReplacedByToken` dropped, and expired tokens deleted by the retention worker.
  Migration `RefreshTokenFamilies`.
- Combined: no overlapping files; full suite 624 passed, 0 failed (11 m 51 s); web build and lint
  pass. Example appsettings add `Retention:RefreshTokenRetention` and
  `Health:ConsumerMaxProcessingTime`.

### 2026-11-03 (Phase 2)
- Roadmap Phase 2, three parallel agents in separate worktrees, combined on `integration/phase2`.
- `feat/health-checks` (OPS-2): API `/health/live` and `/health/ready` (database and broker),
  anonymous and not rate-limited. Workers keep a heartbeat file fresh while healthy, and compose
  checks it. The poller is unhealthy only when it misses polls while the market is open; a broker
  outage doesn't mark any worker unhealthy.
- `feat/sql-retry-http-timeouts` (REL-2, REL-3), plan approved first because it touches the
  AlertWorker consumer:
  - SQL retry strategy (3 retries, 5 s cap). `StockPriceConsumer` and `AlertEvaluationService` run
    their transactions as replayable execution-strategy units. Unique keys, IDs set in code and the
    conditional updates already prevent duplicates after an ambiguous commit; verified saves make
    the poller, notification insert and alert create report success instead of failing.
  - Short SQL blips no longer use up a queue retry; longer outages retry and dead-letter as before.
  - Vendor HttpClients get timeouts only (20 s, options 30 s), never retries, so a billed request
    is never paid twice. The market calendar no longer uses the 100 s default.
- `feat/hashed-refresh-tokens` (SEC-3): refresh tokens are stored as SHA-256 hashes. The migration
  hashes existing tokens, so nobody has to log in again.
- Combined on `integration/phase2`: no overlapping files; full suite 562 passed, 0 failed (10 m 58 s).
- The example appsettings files now list `Database` (all four), the vendor `Timeout` settings
  (API and PricePollingWorker) and `Health` (workers).

### 2026-11-02 (Phase 1)
- Roadmap Phase 1, two parallel agents in separate worktrees, combined on `integration/phase1`.
- `feat/outbox-index-retention` (Workstream C):
  - DATA-2: migration `OutboxIndexesAndRetention`. `OutboxMessage.Type` is now `nvarchar(200)`,
    with filtered `IX_OutboxMessages_Pending` (CreatedAt INCLUDE Type, unprocessed rows) and
    `IX_OutboxMessages_Processed`, and an index on `ProcessedMessages.ProcessedAt`.
  - DATA-1: `DataRetentionWorker` in the OutboxWorker deletes, hourly and in batches, processed
    outbox rows and `ProcessedMessages` after 7 days and `StockPrices` after 30 days, keeping each
    symbol's latest price.
  - messaging-reviewer: no defects. I fixed its low findings: upper bounds on the retention options
    (an out-of-range delay could stop the host), no false failure logged on shutdown, and a 100 ms
    pause between full batches.
- `feat/messaging-metrics-dlq` (Workstream D):
  - OPS-1: the `WaxyCandles` meter (outbox backlog, queue and dead-letter depth, consumer outcomes,
    poll health, alerts fired, Market Data credits from `X-Api-Ratelimit-Consumed`).
  - MSG-1: retry and dead-letter publishes keep `MessageId`/`Type`, and dead-letters say where, why
    and when.
  - MSG-2: consumer prefetch of 10.
  - messaging-reviewer: no defect that loses or duplicates a message. I fixed its findings:
    - a dead-letter now records retries in `x-dead-letter-retry-count`, not the live
      `x-retry-count`, so a message moved back to its queue gets a full retry budget;
    - consumer metric recording can't throw into message handling;
    - an invalid `RabbitMQ:PrefetchCount` warns and uses 10 instead of stopping the consumer;
    - the queue-depth refresh has a 5 s limit and is skipped while the publish channel is down;
    - dead-letters record the root exception.
- The example appsettings files now list `Retention` (OutboxWorker) and `RabbitMQ:PrefetchCount`
  (API and AlertWorker).

### 2026-11-02
- Architecture review and a prioritized roadmap. I found no P0 issue for the current local-only
  deployment. Two items would become P0 once the app is internet-facing: GEX/history credit
  exhaustion and login brute force.
- Phase 0 was done by two parallel agents in separate worktrees, then merged on `integration/phase0`.
- `fix/outbox-startup-and-routing` (Workstream B):
  - REL-1: the OutboxWorker no longer fails to start while RabbitMQ is down; it connects lazily and
    retries every poll.
  - MSG-3: an alert's outbox `CreatedAt` is now its write time, like price rows.
  - MSG-4: stable `MessageType` constants with the old strings, so pending rows still route.
  - No retry, dead-letter or idempotency change (messaging-reviewer: no defects).
- `feat/api-security-hardening` (Workstream A):
  - SEC-1: built-in rate limiting (GEX and alert create/update 30/min per user, history import
    5/min per user); GEX chains fetched and cached by a ladder window (`GexFetchWindow`), so
    near-identical requests can't each cost a billed fetch.
  - SEC-2: login lockout (5 failures, then 5 min, same 401 as a wrong password) and an auth limit of
    10/min per IP.
  - CFG-1: `Cors:AllowedOrigins` (required), Swagger only in Development, `Jwt:Secret` validated
    (≥ 32 bytes), optional RabbitMQ credentials and vhost, CORS before authentication.
- Example appsettings files updated with the new keys. A `Jwt:Secret` shorter than 32 bytes in
  user secrets now stops the API from starting.

### 2026-10-28
- Watchlist shows a newly added symbol immediately. `GET /api/watchlist/quotes` used to build its list
  from stored `StockPrices`, so a symbol added to the watchlist stayed hidden until the poller's next
  run (up to a minute, or until the next session outside market hours). It now lists every
  watchlist symbol in `AddedAt` order, with nullable `Price`/`Change`/`ChangePercent` until a first
  price exists. The page shows "—" and refetches every 30s while any price is missing. No change to
  polling or messaging.
- Session refresh: nothing called `POST /api/auth/refresh`, so the 15-minute access cookie expired
  and every request 401'd. The axios client (`web/src/api/client.ts`) now refreshes on a 401 and
  retries the request once; concurrent 401s share one refresh, since the API rotates the refresh
  token. If the refresh is rejected, it retries once more, in case another tab rotated the token
  first, then sends the user to `/login`. The notifications hub refreshes when it reconnects. Not
  yet tested in a browser.
- Found while running `docker-compose.app.yml`: Market Data answered polls with 203 and quotes at
  the previous close (2026-10-27 16:01 ET) during regular hours, so the "only newer" check stored one
  price per symbol and none after it, and alerts never fired. Confirmed in Market Data's Data
  Freshness docs: `stocks/prices` is real-time only on paid plans; free and trial accounts get
  "Historical (24-hour delayed)" data. The 2026-10-15 entry's "real-time on every plan" was wrong:
  it came from the `stocks/prices` endpoint page, which the plan and Data Freshness pages
  contradict. Corrected the provider and test comments. No code change needed after upgrading: the default mode is live on paid plans.
- Intraday charts (1H/30m/15m/5m) were always empty: `StockCandlestickChart` cleared its data for
  any timeframe but 1D, although the API returned the candles. Now drawn, with times on the axis,
  in Eastern wall-clock time (lightweight-charts shows UTC, so intraday times are shifted). Also
  fixed: the hover tooltip looked candles up from the first data it saw (the long-standing lint
  warning), and daily tooltips showed the day before (UTC-midnight dates formatted in local time).
- `scripts/fake-price.ps1 <SYMBOL> <PRICE> [-DryRun]`: writes a current-timestamped price and its
  outbox row into the `docker-compose.app.yml` database, to test alerts and notifications end to
  end without a live feed.

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
- Tracing merged and confirmed working locally (traces visible in the dashboard).
- `chore/small-fixes`: Dashboard "Market Status" card now real (`GET /api/market/status`, same
  calendar as the poller: open/closed and next open/close), route-level code splitting (largest
  chunk 327 KB, was one 912 KB bundle), CLAUDE.md test-suite description updated.

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
