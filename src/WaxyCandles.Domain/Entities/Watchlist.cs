namespace WaxyCandles.Domain.Entities;

public class Watchlist
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = default!;

    public string Symbol { get; set; } = default!;

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}