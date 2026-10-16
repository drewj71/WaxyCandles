namespace WaxyCandles.Application.MarketData.Models;

/// <summary>US regular-session status.</summary>
/// <param name="IsOpen">Within the 09:30-16:00 ET session of a trading day.</param>
/// <param name="NextChange">
/// When that changes: today's 16:00 ET close if open, else the next trading
/// day's 09:30 ET open. Early closes (half days) aren't modelled.
/// </param>
public sealed record MarketStatus(
    bool IsOpen,
    DateTimeOffset NextChange);
