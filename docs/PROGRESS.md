# Progress log

Running record of what's changed and what's planned next. Newest entries first.
Update this at the end of each working session.

## Next up

- [ ] **Push `feat/marketdata-app-provider` and open a PR** (tested end to end 2026-10-15).
- [ ] **Stamp prices with the quote's own time, not poll time.** `MarketDataService` sets
  `StockPrice.Timestamp` / `StockPriceUpdatedMessage.Timestamp` to `UtcNow`, but MarketData.app
  timestamps should come from the quote's `updated` field. Alert ordering (`StateChangedAt` vs. observation time) relies on this
  being when the price was observed. Needs a quote timestamp on `MarketQuote`.
- [ ] **Options data** via MarketData.app: separate `IOptionsDataProvider` (not a bigger
  `IMarketDataProvider`). Filter chains by strike/expiration, as each contract returned costs a credit.
- [ ] Batch quote requests in `MarketDataService` (MarketData.app takes many symbols per call
  at the same credit cost) if the watched symbol count grows.
- [ ] **Schwab market data provider**: developer account applied for, access **pending** (as of 2026-10-14).
  Lower priority now that MarketData.app `stocks/prices` is real-time.
  Open questions:
  - OAuth 2.0 with browser login: ~30 min access tokens, ~7 day refresh tokens, so weekly manual
    re-auth. Need a token store and a re-auth story for the headless `PricePollingWorker`.
  - Schwab streaming (WebSocket) vs. keeping the current poll → outbox pipeline.

## Log

### 2026-10-15
- Tested MarketData.app end to end locally: AAPL history import worked and a polled
  quote triggered an alert notification.
- Docs check: `stocks/quotes` is not real-time (trial/free get the last closed session, paid
  plans 15-min delayed without exchange agreements), so that alert likely fired on the previous
  close. Switched quotes to `stocks/prices`, real-time SmartMid (bid/ask midpoint) on every
  plan, same 1 credit per symbol, no volume.

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
