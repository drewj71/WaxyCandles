namespace WaxyCandles.Application.Watchlists.Models;

public class WatchlistStockDto
{
    public string Symbol { get; set; } = default!;
    public decimal Price { get; set; }
    public decimal Change { get; set; }
    public decimal ChangePercent { get; set; }
}