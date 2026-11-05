using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WaxyCandles.Api.RateLimiting;
using WaxyCandles.Application.Backtesting;

namespace WaxyCandles.Api.Controllers;

[ApiController]
[Route("api/backtests")]
[Authorize]
public class BacktestsController : ControllerBase
{
    private readonly IBacktestService _backtestService;

    public BacktestsController(IBacktestService backtestService)
    {
        _backtestService = backtestService;
    }

    /// <summary>
    /// Runs a long-only strategy and the buy-and-hold benchmark on daily
    /// candles. Missing history is imported first (a billed provider request),
    /// so this shares the history import's per-user limit.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.HistoryImport)]
    public async Task<ActionResult<BacktestResponse>> Run(
        BacktestRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _backtestService.RunAsync(request, cancellationToken);

        if (response is null)
        {
            return NotFound(new
            {
                message = $"No daily candles for {request.Symbol.Trim().ToUpperInvariant()} in that date range."
            });
        }

        return Ok(response);
    }
}
