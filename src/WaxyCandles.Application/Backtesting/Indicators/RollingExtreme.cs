namespace WaxyCandles.Application.Backtesting.Indicators;

/// <summary>
/// The highest (or lowest) of the previous <c>period</c> values, not counting
/// the one being added: <see cref="Add"/> returns the extreme before adding.
/// Null until <c>period</c> values have been seen.
/// </summary>
public sealed class RollingExtreme(int period, bool highest)
{
    private readonly Queue<decimal> _window = new();

    public decimal? Add(decimal value)
    {
        decimal? previous = _window.Count == period
            ? highest ? _window.Max() : _window.Min()
            : null;

        _window.Enqueue(value);

        if (_window.Count > period)
        {
            _window.Dequeue();
        }

        return previous;
    }
}
