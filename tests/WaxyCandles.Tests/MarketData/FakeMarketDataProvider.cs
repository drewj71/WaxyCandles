using System.Collections.Concurrent;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Application.MarketData.Models;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Tests.MarketData;

/// <summary>
/// In-memory IMarketDataProvider: canned quotes and candles, optional failures,
/// and a record of which symbols were requested. No network calls.
/// </summary>
public sealed class FakeMarketDataProvider : IMarketDataProvider
{
    private readonly ConcurrentQueue<string> _quoteRequests = new();

    public Dictionary<string, MarketQuote?> Quotes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> FailingSymbols { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<StockCandle> Candles { get; set; } = [];

    public IReadOnlyCollection<string> QuoteRequests => _quoteRequests.ToArray();

    public void SetQuote(string symbol, decimal price) =>
        Quotes[symbol] = new MarketQuote
        {
            Symbol = symbol,
            Price = price,
            Change = 1.5m,
            ChangePercent = 0.75m
        };

    public Task<MarketQuote?> GetCurrentQuoteAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        _quoteRequests.Enqueue(symbol);

        if (FailingSymbols.Contains(symbol))
        {
            throw new HttpRequestException($"Simulated provider failure for {symbol}");
        }

        return Task.FromResult(Quotes.GetValueOrDefault(symbol));
    }

    public Task<IReadOnlyList<StockCandle>> GetHistoricalCandlesAsync(
        string symbol,
        CandleInterval interval,
        CancellationToken cancellationToken = default)
    {
        // Fresh instances each call, like a real provider deserializing a response.
        IReadOnlyList<StockCandle> candles = Candles
            .Select(c => new StockCandle
            {
                Id = Guid.NewGuid(),
                Symbol = c.Symbol,
                Interval = c.Interval,
                Timestamp = c.Timestamp,
                Open = c.Open,
                High = c.High,
                Low = c.Low,
                Close = c.Close,
                Volume = c.Volume
            })
            .ToList();

        return Task.FromResult(candles);
    }
}
