using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.Alerts.DTOs;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Api;

public class AlertLifecycleApiTests(IntegrationFixture fixture) : ApiTestBase(fixture)
{
    private ApiClient _alice = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _alice = await RegisteredClientAsync("alice@example.com");
    }

    [Fact]
    public async Task New_alert_defaults_to_an_enabled_one_time_alert()
    {
        // The request shape the current frontend sends: no lifecycle fields.
        var created = await CreateAsync(new { symbol = "aapl", targetPrice = 150, direction = "Above" });

        Assert.Equal("AAPL", created.Symbol);
        Assert.Equal(AlertFrequency.Once, created.Frequency);
        Assert.Equal(15, created.CooldownMinutes);
        Assert.True(created.IsEnabled);
        Assert.Null(created.ExpiresAt);
        Assert.Equal(AlertStatus.Active, created.Status);
        Assert.Equal(0, created.TriggerCount);
        Assert.Null(created.TriggeredAt);
    }

    [Fact]
    public async Task Repeating_alert_with_cooldown_and_expiry_is_created_as_requested()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddDays(7);

        var created = await CreateAsync(new
        {
            symbol = "MSFT",
            targetPrice = 0.0525m,
            direction = "Below",
            frequency = "EveryCross",
            cooldownMinutes = 60,
            expiresAt
        });

        Assert.Equal(0.0525m, created.TargetPrice);
        Assert.Equal(AlertFrequency.EveryCross, created.Frequency);
        Assert.Equal(60, created.CooldownMinutes);
        Assert.Equal(expiresAt, created.ExpiresAt);

        var fetched = await _alice.Http.GetFromJsonAsync<AlertResponse>($"/api/alerts/{created.Id}", Json);
        Assert.Equal(AlertFrequency.EveryCross, fetched!.Frequency);
    }

    public static TheoryData<string, string> InvalidCreates => new()
    {
        { """{ "symbol": "", "targetPrice": 150, "direction": "Above" }""", "symbol" },
        { """{ "symbol": "AA PL", "targetPrice": 150, "direction": "Above" }""", "symbol" },
        { """{ "symbol": "WAYTOOLONGSYM", "targetPrice": 150, "direction": "Above" }""", "symbol" },
        { """{ "symbol": "AAPL", "targetPrice": 0, "direction": "Above" }""", "targetPrice" },
        { """{ "symbol": "AAPL", "targetPrice": -5, "direction": "Above" }""", "targetPrice" },
        { """{ "symbol": "AAPL", "targetPrice": 150, "direction": 0 }""", "direction" },
        { """{ "symbol": "AAPL", "targetPrice": 150, "direction": "Above", "frequency": 9 }""", "frequency" },
        { """{ "symbol": "AAPL", "targetPrice": 150, "direction": "Above", "cooldownMinutes": 0 }""", "cooldownMinutes" },
        { """{ "symbol": "AAPL", "targetPrice": 150, "direction": "Above", "cooldownMinutes": 1441 }""", "cooldownMinutes" },
        { """{ "symbol": "AAPL", "targetPrice": 150, "direction": "Above", "expiresAt": "2020-01-01T00:00:00Z" }""", "expiresAt" }
    };

    [Theory]
    [MemberData(nameof(InvalidCreates))]
    public async Task Invalid_alert_is_rejected_with_the_offending_field(string body, string field)
    {
        var response = await _alice.Http.PostAsync(
            "/api/alerts",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(
            problem.GetProperty("errors").TryGetProperty(field, out _),
            $"Expected an error for '{field}' in {problem}");

        await using var db = NewDbContext();
        Assert.Equal(0, await db.Alerts.CountAsync());
    }

    [Fact]
    public async Task Changing_the_target_rearms_a_fired_alert_and_keeps_its_history()
    {
        var created = await CreateAsync(new { symbol = "AAPL", targetPrice = 150, direction = "Above" });
        var firedAt = await MarkFiredAsync(created.Id);

        var updated = await UpdateAsync(created.Id, new { targetPrice = 160, direction = "Above" });

        Assert.Equal(AlertStatus.Active, updated.Status);
        Assert.False(updated.IsTriggered);
        Assert.Equal(1, updated.TriggerCount);
        Assert.Equal(firedAt, updated.TriggeredAt);
    }

    [Fact]
    public async Task Disabling_a_fired_alert_does_not_rearm_it()
    {
        var created = await CreateAsync(new { symbol = "AAPL", targetPrice = 150, direction = "Above" });
        await MarkFiredAsync(created.Id);

        var disabled = await UpdateAsync(created.Id, new { targetPrice = 150, direction = "Above", isEnabled = false });

        Assert.Equal(AlertStatus.Disabled, disabled.Status);
        Assert.True(disabled.IsTriggered);

        var enabled = await UpdateAsync(created.Id, new { targetPrice = 150, direction = "Above", isEnabled = true });

        Assert.Equal(AlertStatus.Triggered, enabled.Status);
    }

    [Fact]
    public async Task Update_replaces_the_settings_so_an_omitted_expiry_is_cleared()
    {
        var created = await CreateAsync(new
        {
            symbol = "AAPL",
            targetPrice = 150,
            direction = "Above",
            expiresAt = DateTimeOffset.UtcNow.AddDays(1)
        });

        var updated = await UpdateAsync(created.Id, new { targetPrice = 150, direction = "Above" });

        Assert.Null(updated.ExpiresAt);
    }

    [Fact]
    public async Task Alert_past_its_expiry_reports_expired()
    {
        var created = await CreateAsync(new
        {
            symbol = "AAPL",
            targetPrice = 150,
            direction = "Above",
            expiresAt = DateTimeOffset.UtcNow.AddDays(1)
        });

        await using (var db = NewDbContext())
        {
            var alert = await db.Alerts.SingleAsync(a => a.Id == created.Id);
            alert.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var alerts = await _alice.Http.GetFromJsonAsync<List<AlertResponse>>("/api/alerts", Json);
        var expired = Assert.Single(alerts!);

        Assert.Equal(AlertStatus.Expired, expired.Status);

        // Sending the (past) expiry back unchanged is allowed, so an expired
        // alert can still be disabled or edited...
        var disabled = await UpdateAsync(created.Id, new
        {
            targetPrice = 150,
            direction = "Above",
            expiresAt = expired.ExpiresAt,
            isEnabled = false
        });
        Assert.Equal(AlertStatus.Disabled, disabled.Status);

        // ...but setting a different past expiry is not.
        var response = await _alice.Http.PutAsJsonAsync(
            $"/api/alerts/{created.Id}",
            new { targetPrice = 150, direction = "Above", expiresAt = expired.ExpiresAt!.Value.AddDays(-1) },
            Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_update_is_rejected_and_leaves_the_alert_unchanged()
    {
        var created = await CreateAsync(new { symbol = "AAPL", targetPrice = 150, direction = "Above" });

        var response = await _alice.Http.PutAsJsonAsync(
            $"/api/alerts/{created.Id}",
            new { targetPrice = -1, direction = "Above" },
            Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var fetched = await _alice.Http.GetFromJsonAsync<AlertResponse>($"/api/alerts/{created.Id}", Json);
        Assert.Equal(150m, fetched!.TargetPrice);
    }

    private async Task<AlertResponse> CreateAsync(object request)
    {
        var response = await _alice.Http.PostAsJsonAsync("/api/alerts", request, Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AlertResponse>(Json))!;
    }

    private async Task<AlertResponse> UpdateAsync(Guid id, object request)
    {
        var response = await _alice.Http.PutAsJsonAsync($"/api/alerts/{id}", request, Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AlertResponse>(Json))!;
    }

    // As the AlertWorker leaves a fired alert.
    private async Task<DateTimeOffset> MarkFiredAsync(Guid id)
    {
        var firedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        await using var db = NewDbContext();
        var alert = await db.Alerts.SingleAsync(a => a.Id == id);
        alert.IsTriggered = true;
        alert.TriggeredAt = firedAt;
        alert.TriggerCount = 1;
        await db.SaveChangesAsync();

        return firedAt;
    }
}
