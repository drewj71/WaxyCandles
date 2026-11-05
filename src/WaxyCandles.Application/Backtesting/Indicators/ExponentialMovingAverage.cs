namespace WaxyCandles.Application.Backtesting.Indicators;

/// <summary>
/// EMA with smoothing 2 / (period + 1), seeded with the simple average of the
/// first <c>period</c> values (so it first has a value at the same bar as the
/// SMA). Null until then.
/// </summary>
public sealed class ExponentialMovingAverage(int period) : IMovingAverage
{
    private readonly decimal _smoothing = 2m / (period + 1);
    private readonly SimpleMovingAverage _seed = new(period);

    public decimal? Value { get; private set; }

    public decimal? Add(decimal value)
    {
        Value = Value is decimal previous
            ? previous + (value - previous) * _smoothing
            : _seed.Add(value);

        return Value;
    }
}
