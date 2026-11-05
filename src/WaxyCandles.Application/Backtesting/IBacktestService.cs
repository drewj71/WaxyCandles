namespace WaxyCandles.Application.Backtesting;

public interface IBacktestService
{
    /// <summary>
    /// Validates the request, imports missing daily history (at most one
    /// provider request), and runs the strategy and the benchmark. Null when
    /// fewer than two daily candles are available in the range.
    /// </summary>
    /// <exception cref="Common.Exceptions.RequestValidationException">The request is invalid.</exception>
    /// <exception cref="MarketData.Exceptions.MarketDataUnavailableException">The provider timed out or is out of credits.</exception>
    Task<BacktestResponse?> RunAsync(
        BacktestRequest request,
        CancellationToken cancellationToken = default);
}
