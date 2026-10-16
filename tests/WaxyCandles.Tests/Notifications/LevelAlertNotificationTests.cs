using System.Text.Json;
using WaxyCandles.Application.Alerts.Messages;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.Notifications;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Notifications;

/// <summary>
/// Notification wording for GEX level alerts, and compatibility with
/// AlertTriggered messages written before the Level field existed.
/// </summary>
public class LevelAlertNotificationTests(IntegrationFixture fixture) : DatabaseTestBase(fixture)
{
    [Fact]
    public async Task Level_alert_notification_names_the_level()
    {
        var userId = await SeedUserAsync();

        var result = await CreateAsync(new AlertTriggeredMessage
        {
            MessageId = Guid.NewGuid(),
            AlertId = Guid.NewGuid(),
            UserId = userId,
            Symbol = "SPY",
            Direction = AlertDirection.Above,
            TargetPrice = 606.95m,
            TriggerPrice = 607.1m,
            TriggeredAt = DateTimeOffset.UtcNow,
            Level = GexLevelKind.King,
            LevelPrice = 610m
        });

        Assert.Equal("SPY GEX level alert triggered", result.Title);
        Assert.Equal(
            "SPY rose above $606.95, approaching its King node at $610.00 (now $607.10).",
            result.Body);
    }

    [Fact]
    public async Task Message_written_before_level_alerts_reads_as_a_price_alert()
    {
        var userId = await SeedUserAsync();

        // The payload shape before Level and LevelPrice were added.
        var json = $$"""
            {
              "MessageId": "{{Guid.NewGuid()}}",
              "AlertId": "{{Guid.NewGuid()}}",
              "UserId": "{{userId}}",
              "Symbol": "AAPL",
              "Direction": 1,
              "TargetPrice": 100,
              "TriggerPrice": 150,
              "TriggeredAt": "2026-10-15T14:00:00+00:00"
            }
            """;

        var message = JsonSerializer.Deserialize<AlertTriggeredMessage>(json)!;
        Assert.Null(message.Level);

        var result = await CreateAsync(message);

        Assert.Equal("AAPL alert triggered", result.Title);
        Assert.Equal("AAPL rose above $100.00 (now $150.00).", result.Body);
    }

    private async Task<(string Title, string Body)> CreateAsync(AlertTriggeredMessage message)
    {
        await using var db = NewDbContext();

        var result = await new NotificationService(db).CreateForTriggeredAlertAsync(message);

        Assert.NotNull(result.Notification);
        return (result.Notification.Title, result.Notification.Body);
    }
}
