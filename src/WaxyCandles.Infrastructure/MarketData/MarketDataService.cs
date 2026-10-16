using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Alerts;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.Infrastructure.Telemetry;

namespace WaxyCandles.Infrastructure.MarketData;

public class MarketDataService : IMarketDataService
{
    private readonly AppDbContext _dbContext;
    private readonly IMarketDataProvider _marketDataProvider;

    public MarketDataService(
        AppDbContext dbContext,
        IMarketDataProvider marketDataProvider)
    {
        _dbContext = dbContext;
        _marketDataProvider = marketDataProvider;
    }

    public async Task UpdatePricesAsync(
        CancellationToken cancellationToken = default)
    {
        var alertSymbols = await _dbContext.Alerts
            .Where(AlertQueries.IsWatching(DateTimeOffset.UtcNow))
            .Select(a => a.Symbol)
            .Distinct()
            .ToListAsync(cancellationToken);

        var watchlistSymbols = await _dbContext.Watchlists
            .Select(w => w.Symbol)
            .Distinct()
            .ToListAsync(cancellationToken);

        var symbols = alertSymbols
            .Union(watchlistSymbols)
            .ToList();

        if (symbols.Count == 0)
            return;

        var quotes =
            await _marketDataProvider.GetCurrentQuotesAsync(
                symbols,
                cancellationToken);

        var quotesBySymbol = quotes
            .ToDictionary(
                x => x.Symbol,
                StringComparer.OrdinalIgnoreCase);

        var latestStored = await _dbContext.StockPrices
            .Where(x => symbols.Contains(x.Symbol))
            .GroupBy(x => x.Symbol)
            .Select(g => new
            {
                Symbol = g.Key,
                Timestamp = g.Max(x => x.Timestamp)
            })
            .ToDictionaryAsync(
                x => x.Symbol,
                x => x.Timestamp,
                StringComparer.OrdinalIgnoreCase,
                cancellationToken);

        var polledAt = DateTimeOffset.UtcNow;

        foreach (var symbol in symbols)
        {
            if (!quotesBySymbol.TryGetValue(symbol, out var quote))
                continue;

            // When the price was observed, not when we asked for it. Alert
            // evaluation orders prices against alert edits by this time, so a
            // quote that is minutes old must not pass for a fresh one. Providers
            // that don't report it fall back to the poll time.
            var timestamp = quote.QuotedAt ?? polledAt;

            // The same quote as last time (no trade or quote change since, or the
            // market is closed): storing and publishing it again would only
            // re-evaluate alerts against a price they have already seen.
            if (latestStored.TryGetValue(symbol, out var latest) &&
                timestamp <= latest)
                continue;

            // One span per symbol, so each price's journey (outbox, alert
            // evaluation, notification) is its own branch of the poll's trace.
            using var activity = WaxyCandlesTelemetry.Source.StartActivity("store price");
            activity?.SetTag(WaxyCandlesTelemetry.Symbol, symbol);

            var stockPrice = new StockPrice
            {
                Id = Guid.NewGuid(),
                Symbol = symbol,
                Price = quote.Price,
                Change = quote.Change,
                ChangePercent = quote.ChangePercent,
                Timestamp = timestamp
            };

            var message = new StockPriceUpdatedMessage
            {
                MessageId = Guid.NewGuid(),
                Symbol = symbol,
                Price = quote.Price,
                Timestamp = timestamp
            };

            var outboxMessage = new OutboxMessage
            {
                Id = message.MessageId,
                Type = nameof(StockPriceUpdatedMessage),
                Payload = JsonSerializer.Serialize(message),
                CreatedAt = polledAt
            };

            activity?.SetTag(WaxyCandlesTelemetry.MessagingMessageId, message.MessageId.ToString());
            MessageTracing.Stamp(outboxMessage);

            _dbContext.StockPrices.Add(stockPrice);
            _dbContext.OutboxMessages.Add(outboxMessage);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}