using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WaxyCandles.Application.Notifications.DTOs;
using WaxyCandles.Application.Notifications.Interfaces;

namespace WaxyCandles.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/notifications")]
public class NotificationsController : ControllerBase
{
    private const int RecentCount = 20;

    private readonly INotificationService _notificationService;

    public NotificationsController(
        INotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    [HttpGet]
    public async Task<ActionResult<NotificationListResponse>> GetRecent(
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        return Ok(await _notificationService.GetRecentAsync(
            userId,
            RecentCount,
            cancellationToken));
    }

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(
        Guid id,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var found = await _notificationService.MarkReadAsync(
            id,
            userId,
            cancellationToken);

        return found ? NoContent() : NotFound();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        await _notificationService.MarkAllReadAsync(
            userId,
            cancellationToken);

        return NoContent();
    }
}
