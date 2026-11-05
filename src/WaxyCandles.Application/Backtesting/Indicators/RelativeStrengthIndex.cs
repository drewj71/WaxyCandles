namespace WaxyCandles.Application.Backtesting.Indicators;

/// <summary>
/// Wilder's RSI on closes. The first value comes after <c>period</c> changes
/// (bar index <c>period</c>), from the simple average gain and loss; later ones
/// use Wilder's smoothing, avg = (avg × (period − 1) + current) / period.
/// No losses gives 100, no movement at all gives 50.
/// </summary>
public sealed class RelativeStrengthIndex(int period)
{
    private decimal? _previousClose;
    private int _changes;
    private decimal _gainSum;
    private decimal _lossSum;
    private decimal _averageGain;
    private decimal _averageLoss;

    public decimal? Value { get; private set; }

    public decimal? Add(decimal close)
    {
        if (_previousClose is not decimal previous)
        {
            _previousClose = close;
            return null;
        }

        _previousClose = close;

        var change = close - previous;
        var gain = Math.Max(change, 0m);
        var loss = Math.Max(-change, 0m);
        _changes++;

        if (_changes < period)
        {
            _gainSum += gain;
            _lossSum += loss;
            return null;
        }

        if (_changes == period)
        {
            _averageGain = (_gainSum + gain) / period;
            _averageLoss = (_lossSum + loss) / period;
        }
        else
        {
            _averageGain = (_averageGain * (period - 1) + gain) / period;
            _averageLoss = (_averageLoss * (period - 1) + loss) / period;
        }

        Value = _averageLoss == 0m
            ? _averageGain == 0m ? 50m : 100m
            : 100m - 100m / (1m + _averageGain / _averageLoss);

        return Value;
    }
}
