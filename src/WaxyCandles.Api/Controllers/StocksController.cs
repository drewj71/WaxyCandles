using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Application.MarketData.Models;
using WaxyCandles.Application.Options.Gex;
using WaxyCandles.Application.Options.Interfaces;
using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Api.Controllers;

[ApiController]
[Route("api/stocks")]
[Authorize]
public class StocksController : ControllerBase
{
    private readonly IHistoricalMarketDataService _historicalMarketDataService;
    private readonly IGexService _gexService;

    public StocksController(
        IHistoricalMarketDataService historicalMarketDataService,
        IGexService gexService)
    {
        _historicalMarketDataService = historicalMarketDataService;
        _gexService = gexService;
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

    /// <summary>
    /// Dealer gamma and vanna exposure by strike and expiration, for strikes
    /// within <paramref name="strikeRange"/>% of the price and expirations up
    /// to <paramref name="days"/> calendar days out.
    /// </summary>
    [HttpGet("{symbol}/gex")]
    public async Task<IActionResult> GetGexTable(
        string symbol,
        CancellationToken cancellationToken,
        [FromQuery] int days = GexRequest.DefaultDays,
        [FromQuery] decimal strikeRange = GexRequest.DefaultStrikeRangePercent)
    {
        var table = await _gexService.GetGexTableAsync(
            symbol,
            new GexRequest(days, strikeRange),
            cancellationToken);

        if (table is null)
        {
            return NotFound(new
            {
                message = $"No option data for {symbol.Trim().ToUpperInvariant()}."
            });
        }

        return Ok(table);
    }
}
