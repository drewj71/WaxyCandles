# WaxyCandles

A full-stack stock alert and market-data platform built with **ASP.NET Core 9, Entity Framework Core, SQL Server, RabbitMQ, and background workers**.

The project started as a learning project focused on **cloud architecture and distributed systems**, while also evolving toward a personal trading/charting platform with plans for technical analysis, options data, GEX/VEX analytics, news, and AI-assisted market analysis.

> **Project status:** Active development

---

## Overview

WaxyCandles allows authenticated users to:

* Create and manage stock price alerts
* Maintain a personal stock watchlist
* View current stock quotes
* Retrieve and store historical OHLCV data
* View historical candle data by timeframe
* Process market-price updates asynchronously
* Evaluate alerts using background workers
* Reliably process messages using RabbitMQ
* Handle failed messages with retries and dead-letter queues
* Prevent duplicate message processing
* Persist outbound events using the Outbox Pattern

The long-term goal is to turn the application into a more complete trading and charting platform.

---

## Architecture

The application is split into multiple services/projects to explore distributed-system patterns and asynchronous processing.

```text
                         ┌─────────────────────┐
                         │    React Frontend   │
                         └──────────┬──────────┘
                                    │
                                    ▼
                         ┌─────────────────────┐
                         │    ASP.NET Core API │
                         └──────────┬──────────┘
                                    │
                    ┌───────────────┼───────────────┐
                    │               │               │
                    ▼               ▼               ▼
                SQL Server     Market Data      RabbitMQ
                    │             Provider           │
                    │                                │
                    ▼                                ▼
              OutboxMessage                  Alert Worker
                    │                                │
                    ▼                                ▼
              Outbox Worker                  Alert Evaluation
                    │
                    ▼
                RabbitMQ
```

### Projects

```text
WaxyCandles
├── src
│   ├── WaxyCandles.Api
│   ├── WaxyCandles.Application
│   ├── WaxyCandles.Domain
│   ├── WaxyCandles.Infrastructure
│   ├── WaxyCandles.PricePollingWorker
│   ├── WaxyCandles.AlertWorker
│   └── WaxyCandles.OutboxWorker
```

### Project responsibilities

**WaxyCandles.Api**

ASP.NET Core Web API responsible for authentication, alerts, watchlists, stocks, and HTTP endpoints.

**WaxyCandles.Application**

Application-level interfaces, DTOs, services, and business operations.

**WaxyCandles.Domain**

Core domain entities and enums independent of infrastructure concerns.

**WaxyCandles.Infrastructure**

EF Core persistence, market-data integrations, RabbitMQ messaging, and other infrastructure implementations.

**WaxyCandles.PricePollingWorker**

Background service responsible for periodically retrieving market prices.

**WaxyCandles.OutboxWorker**

Reads unprocessed messages from the database Outbox and publishes them to RabbitMQ.

**WaxyCandles.AlertWorker**

Consumes market-price messages from RabbitMQ and evaluates matching user alerts.

---

# Technology Stack

### Backend

* .NET 9
* ASP.NET Core Web API
* Entity Framework Core
* ASP.NET Core Identity
* JWT authentication
* SQL Server

### Distributed Systems

* RabbitMQ
* Background workers
* Outbox Pattern
* Retry queues
* Dead-letter queues
* Idempotent message processing

### Market Data

* Alpha Vantage
* Historical OHLCV data
* Current stock quotes

The market-data layer uses an abstraction so providers can be replaced or added without coupling the application to a specific vendor.

```csharp
public interface IMarketDataProvider
{
    Task<MarketQuote?> GetCurrentQuoteAsync(
        string symbol,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StockCandle>> GetHistoricalCandlesAsync(
        string symbol,
        CandleInterval interval,
        CancellationToken cancellationToken = default);
}
```

---

# Authentication

The API uses ASP.NET Core Identity for user management and JWT-based authentication.

Implemented functionality includes:

* User registration
* Login
* Logout
* Refresh tokens
* Authenticated user information
* Protected API endpoints

Alerts and watchlists are scoped to the authenticated user.

---

# Stock Alerts

Users can create price-based alerts for individual stocks.

An alert contains:

```text
Symbol
Target Price
Direction
Triggered Status
Created At
User
```

Supported alert directions include:

* Price reaches or exceeds target
* Price reaches or falls below target

Example:

```text
AAPL
Target: $350
Direction: Above
```

When a market-price update is received, the Alert Worker evaluates matching untriggered alerts.

---

# Watchlists

Users can maintain their own stock watchlist.

Supported operations:

```text
GET    /api/watchlist
GET    /api/watchlist/quotes
POST   /api/watchlist/{symbol}
DELETE /api/watchlist/{symbol}
```

The watchlist prevents duplicate symbols per user using a unique database index on:

```text
UserId + Symbol
```

The quote endpoint retrieves current market data for the user's watchlist.

---

# Historical Market Data

Historical stock data is stored as OHLCV candles.

Each candle contains:

```text
Symbol
Interval
Timestamp
Open
High
Low
Close
Volume
```

Supported candle intervals are:

```text
5 minutes
15 minutes
30 minutes
1 hour
1 day
```

The current Alpha Vantage integration provides daily historical data. Intraday intervals are modeled in the application so additional market-data providers can be integrated later.

Historical data is exposed through:

```text
POST /api/stocks/{symbol}/historical
GET  /api/stocks/{symbol}/historical
```

For example:

```text
GET /api/stocks/AAPL/historical?interval=4
```

where `4` represents the daily interval.

Historical candles are deduplicated using:

```text
Symbol + Interval + Timestamp
```

and the database also enforces uniqueness.

---

# Asynchronous Market Data Processing

One of the primary goals of this project is learning how to build systems that don't require every operation to happen synchronously inside an HTTP request.

The market-price processing pipeline uses RabbitMQ and the Outbox Pattern.

```text
Market Data Provider
        │
        ▼
Price Polling Worker
        │
        ▼
StockPrice + OutboxMessage
        │
        ▼
      SQL Server
        │
        ▼
   Outbox Worker
        │
        ▼
     RabbitMQ
        │
        ▼
   Alert Worker
        │
        ▼
Alert Evaluation
```

This separates market-data collection from alert processing and allows each component to operate independently.

---

# Outbox Pattern

The application uses an Outbox Pattern to avoid losing events when database operations and message publishing occur separately.

When a market-price update is processed, the application writes both:

```text
StockPrice
OutboxMessage
```

to SQL Server in the same database operation.

The Outbox Worker then publishes the stored message to RabbitMQ.

```text
Database Transaction
┌─────────────────────────┐
│ StockPrice              │
│ OutboxMessage            │
└─────────────────────────┘
             │
             ▼
        Outbox Worker
             │
             ▼
          RabbitMQ
```

This allows the database to act as the durable source of pending messages.

---

# RabbitMQ Retry and Dead-Letter Processing

RabbitMQ messages use retry and dead-letter queues.

Current flow:

```text
stock-price-updated
        │
        ├── success ───────────────► ACK
        │
        └── failure
              │
              ▼
      stock-price-retry
              │
           5 second TTL
              │
              ▼
      stock-price-updated
              │
              └── repeated failures
                        │
                        ▼
              stock-price-dead-letter
```

Messages are retried a limited number of times before being moved to the dead-letter queue.

This prevents permanently failing messages from being retried indefinitely.

---

# Message Idempotency

The Alert Worker tracks processed message IDs.

```text
ProcessedMessage
----------------
Id
ProcessedAt
```

Before processing a message, the worker checks whether its ID has already been processed.

This protects against duplicate delivery, which can occur in distributed systems when a consumer processes a message but fails before acknowledging it.

The goal is **at-least-once message delivery with idempotent consumers**.

---

# Market Data Provider Abstraction

Market data access is intentionally abstracted behind interfaces.

```text
Application
     │
     ▼
IMarketDataProvider
     ▲
     │
Infrastructure
     │
     ├── AlphaVantageMarketDataProvider
     └── Future providers
```

This allows the application to change market-data vendors without changing controllers or business logic.

Additional providers are planned as the project expands into intraday and options data.

---

# Database

Entity Framework Core manages the SQL Server database.

Current domain data includes concepts such as:

```text
Users
Alerts
Watchlists
StockPrices
StockCandles
RefreshTokens
OutboxMessages
ProcessedMessages
```

EF Core migrations are committed to source control so the database schema can be reproduced in another environment.

---

# Running Locally

## Prerequisites

* .NET 9 SDK
* SQL Server
* Docker Desktop
* RabbitMQ
* Alpha Vantage API key

RabbitMQ can be started using Docker Compose.

```bash
docker compose up -d
```

The RabbitMQ management interface is available locally on:

```text
http://localhost:15672
```

---

## Configuration

Copy the example configuration:

```bash
cp appsettings.example.json appsettings.json
```

Then provide your own API keys and local configuration.

Example:

```json
{
  "AlphaVantage": {
    "ApiKey": "YOUR_ALPHA_VANTAGE_API_KEY"
  },
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=WaxyCandles;Trusted_Connection=True;TrustServerCertificate=True;"
  },
  "RabbitMQ": {
    "Host": "localhost"
  }
}
```

**Never commit API keys, passwords, JWT secrets, certificates, or other credentials to source control.**

---

# API Examples

### Get a stock

```http
GET /api/stocks/AAPL
```

Returns current quote information and stored daily historical candles.

### Get historical candles

```http
GET /api/stocks/AAPL/historical?interval=4
```

### Import historical data

```http
POST /api/stocks/AAPL/historical?interval=4
```

### Get watchlist

```http
GET /api/watchlist
```

### Get watchlist quotes

```http
GET /api/watchlist/quotes
```

### Create an alert

```http
POST /api/alerts
```

### Get alerts

```http
GET /api/alerts
```

All protected endpoints require authentication.

---

# Current Status

### Completed

* [x] ASP.NET Core API
* [x] ASP.NET Core Identity
* [x] JWT authentication
* [x] Refresh tokens
* [x] Alert CRUD
* [x] Watchlist CRUD
* [x] Current stock quotes
* [x] Historical daily OHLCV data
* [x] Candle storage
* [x] Historical-data deduplication
* [x] Market-data provider abstraction
* [x] RabbitMQ integration
* [x] Background workers
* [x] Outbox Pattern
* [x] Retry queues
* [x] Dead-letter queues
* [x] Message idempotency

### In Progress / Planned

* [ ] React/TypeScript trading dashboard
* [ ] Interactive candlestick charts
* [ ] Additional intraday market-data provider
* [ ] Technical indicators
* [ ] Options chains
* [ ] Implied volatility analytics
* [ ] GEX calculations
* [ ] VEX calculations
* [ ] Options flow / positioning analytics
* [ ] Market news
* [ ] AI-powered market analysis
* [ ] Redis caching
* [ ] Observability and distributed tracing
* [ ] Production deployment
* [ ] Additional cloud infrastructure

---

# Long-Term Vision

The eventual application is intended to combine traditional charting with options positioning and AI-assisted analysis.

A future stock page may combine:

```text
                    Stock Detail
                         │
        ┌────────────────┼────────────────┐
        │                │                │
    Price Chart      Options Chain      News
        │                │                │
    Indicators          GEX/VEX        Sentiment
        │                │                │
        └────────────────┼────────────────┘
                         │
                    AI Analysis
```

The AI component is intended to provide analysis based on the application's market data, technical indicators, options positioning, and news rather than functioning as a generic chatbot.

Potential use cases include:

* 0DTE market analysis
* Swing-trade research
* Technical setup analysis
* Options positioning analysis
* Identifying significant price/option levels
* Summarizing relevant market news

The system is intended to provide **analysis and context rather than automatically execute trades**.

---

# Why I Built This

This project is both a practical application and a learning exercise in modern backend and distributed-system architecture.

The main engineering goals are to gain hands-on experience with:

* Designing service boundaries
* Background processing
* Message queues
* Event-driven architecture
* Reliable event publishing
* Retry strategies
* Dead-letter queues
* Idempotent consumers
* Database consistency
* External API integrations
* Authentication and authorization
* Containerized development
* Cloud deployment
* Scaling and caching
* Observability

Rather than implementing these technologies in isolation, the goal is to introduce them as the application grows and there is a concrete reason for each architectural decision.

---

# Disclaimer

WaxyCandles is a personal software project for educational and informational purposes.

Market data and AI-generated analysis may be incomplete, delayed, or inaccurate. Nothing provided by the application should be considered financial, investment, or trading advice.
