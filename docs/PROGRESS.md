# Progress log

Running record of what's changed and what's planned next. Newest entries first.
Update this at the end of each working session.

## Next up

- [ ] **Merge `fix/alert-lifecycle-races`** into `master` (only unmerged branch; 1 commit ahead).
- [ ] **Schwab market data provider** — developer account applied for, access **pending** (as of 2026-10-14).
  Open questions to settle once access arrives:
  - OAuth 2.0 with browser login: ~30 min access tokens, ~7 day refresh tokens, so weekly manual
    re-auth. Need a token store and a re-auth story for the headless `PricePollingWorker`.
  - Schwab streaming (WebSocket) vs. keeping the current poll → outbox pipeline.
  - Whether Schwab replaces Alpha Vantage or sits alongside it (e.g. live quotes vs. history).
- [ ] Prep work that doesn't need Schwab access: config-driven provider selection
  (e.g. `MarketData:Provider`) behind `IMarketDataProvider`.

## Log

### 2026-10-14
- Started this progress log. Context from earlier sessions wasn't saved, so the history
  below is reconstructed from git.

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
