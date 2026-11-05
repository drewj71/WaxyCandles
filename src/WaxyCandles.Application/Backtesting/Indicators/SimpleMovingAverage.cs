namespace WaxyCandles.Application.Backtesting.Indicators;

/// <summary>Mean of the last <c>period</c> values; null until that many have been added.</summary>
public sealed class SimpleMovingAverage(int period) : IMovingAverage
{
    private readonly Queue<decimal> _window = new();
    private decimal _sum;

    public decimal? Value { get; private set; }

    public decimal? Add(decimal value)
    {
        _window.Enqueue(value);
        _sum += value;

        if (_window.Count > period)
        {
            _sum -= _window.Dequeue();
        }

        Value = _window.Count == period ? _sum / period : null;
        return Value;
    }
}

public interface IMovingAverage
{
    decimal? Value { get; }

    decimal? Add(decimal value);
}
