using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.Notifications.DTOs;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Api;

/// <summary>
/// The notifications REST endpoints. Every endpoint is scoped to the caller.
/// </summary>
public class NotificationsApiTests(IntegrationFixture fixture) : ApiTestBase(fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 14, 0, 0, TimeSpan.Zero);

    private ApiClient _alice = default!;
    private ApiClient _bob = default!;
    private string _aliceId = default!;
    private string _bobId = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _alice = await RegisteredClientAsync("alice@example.com");
        _bob = await RegisteredClientAsync("bob@example.com");
        _aliceId = await UserIdAsync(_alice);
        _bobId = await UserIdAsync(_bob);
    }

    [Fact]
    public async Task Lists_the_callers_notifications_newest_first_with_unread_count()
    {
        var older = await SeedNotificationAsync(_aliceId, T0, read: true);
        var newer = await SeedNotificationAsync(_aliceId, T0.AddMinutes(5));
        var newest = await SeedNotificationAsync(_aliceId, T0.AddMinutes(10));
        await SeedNotificationAsync(_bobId, T0.AddMinutes(20));

        var list = await GetAsync(_alice);

        Assert.Equal([newest, newer, older], list.Items.Select(n => n.Id));
        Assert.Equal(2, list.UnreadCount);
        Assert.Equal(NotificationType.AlertTriggered, list.Items[0].Type);
    }

    [Fact]
    public async Task Marking_read_sets_read_time_and_lowers_unread_count()
    {
        var id = await SeedNotificationAsync(_aliceId, T0);
        await SeedNotificationAsync(_aliceId, T0.AddMinutes(1));

        var response = await _alice.Http.PostAsync($"/api/notifications/{id}/read", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var list = await GetAsync(_alice);
        Assert.Equal(1, list.UnreadCount);
        Assert.NotNull(list.Items.Single(n => n.Id == id).ReadAt);
    }

    [Fact]
    public async Task Marking_read_twice_keeps_the_first_read_time()
    {
        var id = await SeedNotificationAsync(_aliceId, T0);

        await _alice.Http.PostAsync($"/api/notifications/{id}/read", null);
        var firstReadAt = await ReadAtAsync(id);

        await _alice.Http.PostAsync($"/api/notifications/{id}/read", null);

        Assert.Equal(firstReadAt, await ReadAtAsync(id));
    }

    [Fact]
    public async Task Another_users_notification_cannot_be_marked_read()
    {
        var id = await SeedNotificationAsync(_aliceId, T0);

        var response = await _bob.Http.PostAsync($"/api/notifications/{id}/read", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(await ReadAtAsync(id));
    }

    [Fact]
    public async Task Mark_all_read_only_affects_the_caller()
    {
        await SeedNotificationAsync(_aliceId, T0);
        await SeedNotificationAsync(_aliceId, T0.AddMinutes(1));
        var bobs = await SeedNotificationAsync(_bobId, T0);

        var response = await _alice.Http.PostAsync("/api/notifications/read-all", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, (await GetAsync(_alice)).UnreadCount);
        Assert.Null(await ReadAtAsync(bobs));
    }

    [Fact]
    public async Task Anonymous_requests_are_rejected()
    {
        var http = NewClient().Http;

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("/api/notifications/read-all", null)).StatusCode);
    }

    private static async Task<NotificationListResponse> GetAsync(ApiClient client) =>
        (await client.Http.GetFromJsonAsync<NotificationListResponse>("/api/notifications", Json))!;

    private static async Task<string> UserIdAsync(ApiClient client) =>
        (await client.Http.GetFromJsonAsync<Me>("/api/auth/me", Json))!.Id;

    private async Task<Guid> SeedNotificationAsync(
        string userId,
        DateTimeOffset createdAt,
        bool read = false)
    {
        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = NotificationType.AlertTriggered,
            Title = "AAPL alert triggered",
            Body = "AAPL rose above $100.00 (now $150.00).",
            AlertId = Guid.NewGuid(),
            SourceMessageId = Guid.NewGuid(),
            CreatedAt = createdAt,
            ReadAt = read ? createdAt.AddMinutes(1) : null
        };

        await using var db = NewDbContext();
        db.Notifications.Add(notification);
        await db.SaveChangesAsync();

        return notification.Id;
    }

    private async Task<DateTimeOffset?> ReadAtAsync(Guid id)
    {
        await using var db = NewDbContext();
        return (await db.Notifications.SingleAsync(n => n.Id == id)).ReadAt;
    }

    private sealed record Me(string Id, string Email);
}
