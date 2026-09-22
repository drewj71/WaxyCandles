using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WaxyCandles.Application.Watchlists.Interfaces;

namespace WaxyCandles.Api.Controllers;

[ApiController]
[Route("api/watchlist")]
[Authorize]
public class WatchlistsController : ControllerBase
{
    private readonly IWatchlistService _watchlistService;

    public WatchlistsController(
        IWatchlistService watchlistService)
    {
        _watchlistService = watchlistService;
    }

    [HttpGet]
    public async Task<IActionResult> GetWatchlist(
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(
            ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        var symbols =
            await _watchlistService.GetWatchlistAsync(
                userId,
                cancellationToken);

        return Ok(symbols);
    }

    [HttpPost("{symbol}")]
    public async Task<IActionResult> AddToWatchlist(
        string symbol,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(
            ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        await _watchlistService.AddToWatchlistAsync(
            userId,
            symbol,
            cancellationToken);

        return Ok();
    }

    [HttpDelete("{symbol}")]
    public async Task<IActionResult> RemoveFromWatchlist(
        string symbol,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(
            ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        await _watchlistService.RemoveFromWatchlistAsync(
            userId,
            symbol,
            cancellationToken);

        return NoContent();
    }

    [HttpGet("quotes")]
    public async Task<IActionResult> GetWatchlistWithQuotes(
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(
            ClaimTypes.NameIdentifier);

        if (userId is null)
            return Unauthorized();

        var stocks =
            await _watchlistService.GetWatchlistWithQuotesAsync(
                userId,
                cancellationToken);

        return Ok(stocks);
    }
}