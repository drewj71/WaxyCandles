using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Persistence;

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
        var symbols = await _dbContext.Alerts
            .Where(a => !a.IsTriggered)
            .Select(a => a.Symbol)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var symbol in symbols)
        {
            var quote =
                await _marketDataProvider.GetCurrentQuoteAsync(
                    symbol,
                    cancellationToken);

            if (quote is null)
                continue;

            var timestamp = DateTimeOffset.UtcNow;

            var stockPrice = new StockPrice
            {
                Id = Guid.NewGuid(),
                Symbol = symbol,
                Price = quote.Price,
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
                CreatedAt = DateTimeOffset.UtcNow
            };

            _dbContext.StockPrices.Add(stockPrice);
            _dbContext.OutboxMessages.Add(outboxMessage);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}