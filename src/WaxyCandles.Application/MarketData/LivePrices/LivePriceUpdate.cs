namespace WaxyCandles.Application.MarketData.LivePrices;

/// <summary>
/// A newly stored price, pushed to browsers watching <see cref="Symbol"/>.
/// Change, change % and previous close are the vendor's, stored with the
/// price by the poller (change = price - previous close).
/// </summary>
public sealed record LivePriceUpdate(
    string Symbol,
    decimal Price,
    decimal Change,
    decimal ChangePercent,
    decimal PreviousClose,
    DateTimeOffset Timestamp);
