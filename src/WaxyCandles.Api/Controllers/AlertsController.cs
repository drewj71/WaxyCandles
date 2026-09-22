using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WaxyCandles.Application.Alerts.DTOs;
using WaxyCandles.Application.Alerts.Interfaces;

namespace WaxyCandles.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/alerts")]
public class AlertsController : ControllerBase
{
    private readonly IAlertService _alertService;

    public AlertsController(IAlertService alertService)
    {
        _alertService = alertService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AlertResponse>>> GetAll()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var alerts = await _alertService.GetAllAsync(userId);

        return Ok(alerts);
    }

    [HttpPost]
    public async Task<ActionResult<AlertResponse>> Create(
        CreateAlertRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var response = await _alertService.CreateAsync(userId, request);

        return CreatedAtAction(
            nameof(GetById),
            new { id = response.Id },
            response);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AlertResponse>> GetById(Guid id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var alert = await _alertService.GetByIdAsync(id, userId);

        if (alert is null)
        {
            return NotFound();
        }

        return Ok(alert);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<AlertResponse>> Update(
        Guid id,
        UpdateAlertRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var alert = await _alertService.UpdateAsync(id, userId, request);

        if (alert is null)
        {
            return NotFound();
        }

        return Ok(alert);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        await _alertService.DeleteAsync(id, userId);

        return NoContent();
    }
}