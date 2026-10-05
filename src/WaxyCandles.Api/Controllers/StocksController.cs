using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Application.MarketData.Models;
using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Api.Controllers;

[ApiController]
[Route("api/stocks")]
[Authorize]
public class StocksController : ControllerBase
{
    private readonly IHistoricalMarketDataService _historicalMarketDataService;

    public StocksController(
        IHistoricalMarketDataService historicalMarketDataService)
    {
        _historicalMarketDataService = historicalMarketDataService;
    }

    [HttpPost("{symbol}/historical")]
    public async Task<IActionResult> ImportHistoricalData(
        string symbol,
        [FromQuery] CandleInterval interval,
        CancellationToken cancellationToken)
    {
        await _historicalMarketDataService
            .ImportHistoricalCandlesAsync(
                symbol,
                interval,
                cancellationToken);

        return Ok(new
        {
            message = $"Historical data imported for {symbol.ToUpperInvariant()}."
        });
    }

    [HttpGet("{symbol}/historical")]
    public async Task<IActionResult> GetHistoricalCandles(
        string symbol,
        [FromQuery] CandleInterval interval,
        CancellationToken cancellationToken)
    {
        var candles =
            await _historicalMarketDataService
                .GetHistoricalCandlesAsync(
                    symbol,
                    interval,
                    cancellationToken);

        return Ok(candles);
    }

    [HttpGet("{symbol}")]
    public async Task<IActionResult> GetStockDetails(
        string symbol,
        CancellationToken cancellationToken,
        [FromQuery] CandleInterval interval = CandleInterval.OneDay)
    {
        var stock =
            await _historicalMarketDataService
                .GetStockDetailsAsync(
                    symbol,
                    interval,
                    cancellationToken);

        return Ok(stock);
    }
}