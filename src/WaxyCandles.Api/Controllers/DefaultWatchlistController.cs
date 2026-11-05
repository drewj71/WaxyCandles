using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WaxyCandles.Application.Watchlists.Interfaces;

namespace WaxyCandles.Api.Controllers;

/// <summary>
/// The original single-watchlist API, kept so existing clients keep working.
/// It acts on the user's default list (the first by sort order); adding a
/// symbol creates "My Watchlist" if the user has no list. The multi-list API
/// is <see cref="WatchlistsController"/> (/api/watchlists).
/// </summary>
[ApiController]
[Route("api/watchlist")]
[Authorize]
public class DefaultWatchlistController : ControllerBase
{
    private readonly IWatchlistService _watchlistService;

    public DefaultWatchlistController(
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
            await _watchlistService.GetDefaultWatchlistSymbolsAsync(
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

        await _watchlistService.AddToDefaultWatchlistAsync(
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

        await _watchlistService.RemoveFromDefaultWatchlistAsync(
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
            await _watchlistService.GetDefaultWatchlistQuotesAsync(
                userId,
                cancellationToken);

        return Ok(stocks);
    }
}