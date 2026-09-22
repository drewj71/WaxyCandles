using WaxyCandles.Application.MarketData.Models;

namespace WaxyCandles.Application.MarketData.Models;

public class StockDetailsDto
{
    public string Symbol { get; set; } = default!;

    public MarketQuote? Quote { get; set; }

    public IReadOnlyList<StockCandleDto> HistoricalCandles { get; set; }
        = [];
}