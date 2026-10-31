namespace WaxyCandles.Application.Watchlists.Models;

public class WatchlistStockDto
{
    public string Symbol { get; set; } = default!;

    // Null until the price polling worker stores a first price for the symbol.
    public decimal? Price { get; set; }
    public decimal? Change { get; set; }
    public decimal? ChangePercent { get; set; }
}
