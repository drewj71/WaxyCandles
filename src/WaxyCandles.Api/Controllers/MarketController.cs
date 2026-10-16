using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WaxyCandles.Application.MarketData;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Application.MarketData.Models;

namespace WaxyCandles.Api.Controllers;

[ApiController]
[Route("api/market")]
[Authorize]
public class MarketController : ControllerBase
{
    private readonly IMarketCalendar _marketCalendar;
    private readonly TimeProvider _timeProvider;

    public MarketController(
        IMarketCalendar marketCalendar,
        TimeProvider timeProvider)
    {
        _marketCalendar = marketCalendar;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Whether US markets are open, by the same calendar the price poller uses
    /// (holidays included when the provider has a calendar).
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<MarketStatus>> GetStatus(
        CancellationToken cancellationToken) =>
        Ok(await MarketHours.StatusAsync(
            _timeProvider.GetUtcNow(),
            _marketCalendar,
            cancellationToken));
}
