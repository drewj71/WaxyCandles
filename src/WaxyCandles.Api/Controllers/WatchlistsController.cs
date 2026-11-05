using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WaxyCandles.Application.Watchlists.Interfaces;
using WaxyCandles.Application.Watchlists.Models;

namespace WaxyCandles.Api.Controllers;

/// <summary>
/// The user's named watchlists. Another user's list id is a 404, the same as
/// one that doesn't exist. Limits and invalid input are 400s.
/// </summary>
[ApiController]
[Route("api/watchlists")]
[Authorize]
public class WatchlistsController : ControllerBase
{
    private readonly IWatchlistService _watchlists;

    public WatchlistsController(IWatchlistService watchlists)
    {
        _watchlists = watchlists;
    }

    private string? UserId => User.FindFirstValue(ClaimTypes.NameIdentifier);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WatchlistDto>>> GetAll(
        CancellationToken cancellationToken)
    {
        if (UserId is not { } userId)
        {
            return Unauthorized();
        }

        return Ok(await _watchlists.GetWatchlistsAsync(userId, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WatchlistDto>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (UserId is not { } userId)
        {
            return Unauthorized();
        }

        return OkOrNotFound(await _watchlists.GetWatchlistAsync(userId, id, cancellationToken));
    }

    [HttpPost]
    public async Task<ActionResult<WatchlistDto>> Create(
        CreateWatchlistRequest request,
        CancellationToken cancellationToken)
    {
        if (UserId is not { } userId)
        {
            return Unauthorized();
        }

        var created = await _watchlists.CreateWatchlistAsync(userId, request.Name, cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<WatchlistDto>> Rename(
        Guid id,
        RenameWatchlistRequest request,
        CancellationToken cancellationToken)
    {
        if (UserId is not { } userId)
        {
            return Unauthorized();
        }

        return OkOrNotFound(
            await _watchlists.RenameWatchlistAsync(userId, id, request.Name, cancellationToken));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (UserId is not { } userId)
        {
            return Unauthorized();
        }

        return await _watchlists.DeleteWatchlistAsync(userId, id, cancellationToken)
            ? NoContent()
            : NotFound();
    }

    [HttpPut("order")]
    public async Task<IActionResult> Reorder(
        ReorderWatchlistsRequest request,
        CancellationToken cancellationToken)
    {
        if (UserId is not { } userId)
        {
            return Unauthorized();
        }

        await _watchlists.ReorderWatchlistsAsync(userId, request.Ids, cancellationToken);

        return NoContent();
    }

    [HttpPost("{id:guid}/symbols")]
    public async Task<ActionResult<WatchlistDto>> AddSymbol(
        Guid id,
        AddWatchlistSymbolRequest request,
        CancellationToken cancellationToken)
    {
        if (UserId is not { } userId)
        {
            return Unauthorized();
        }

        return OkOrNotFound(
            await _watchlists.AddSymbolAsync(userId, id, request.Symbol, cancellationToken));
    }

    [HttpDelete("{id:guid}/symbols/{symbol}")]
    public async Task<IActionResult> RemoveSymbol(
        Guid id,
        string symbol,
        CancellationToken cancellationToken)
    {
        if (UserId is not { } userId)
        {
            return Unauthorized();
        }

        return await _watchlists.RemoveSymbolAsync(userId, id, symbol, cancellationToken)
            ? NoContent()
            : NotFound();
    }

    [HttpPut("{id:guid}/symbols/order")]
    public async Task<ActionResult<WatchlistDto>> ReorderSymbols(
        Guid id,
        ReorderWatchlistSymbolsRequest request,
        CancellationToken cancellationToken)
    {
        if (UserId is not { } userId)
        {
            return Unauthorized();
        }

        return OkOrNotFound(
            await _watchlists.ReorderSymbolsAsync(userId, id, request.Symbols, cancellationToken));
    }

    /// <summary>The list's quote table: stored prices, day range, volume and sparkline.</summary>
    [HttpGet("{id:guid}/quotes")]
    public async Task<ActionResult<IReadOnlyList<WatchlistQuoteDto>>> GetQuotes(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (UserId is not { } userId)
        {
            return Unauthorized();
        }

        return OkOrNotFound(await _watchlists.GetQuotesAsync(userId, id, cancellationToken));
    }

    private ActionResult<T> OkOrNotFound<T>(T? value) where T : class =>
        value is null ? NotFound() : Ok(value);
}
