using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using WaxyCandles.Api.Hubs;
using WaxyCandles.Application.Alerts.Messages;
using WaxyCandles.Application.Notifications.DTOs;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.Messaging;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Api;

/// <summary>
/// End to end through the real API host: an AlertTriggered message on RabbitMQ is
/// consumed by the API's hosted AlertTriggeredConsumer and pushed over SignalR to
/// the owner's connection, authenticated with the same cookie as the REST API.
/// </summary>
public class NotificationsHubTests(IntegrationFixture fixture) : ApiTestBase(fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private RabbitMqTestBroker _broker = default!;
    private readonly List<HubConnection> _connections = [];

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _broker = await RabbitMqTestBroker.ConnectAsync(Fixture);
        await _broker.DeleteTopologyAsync();
    }

    public override async Task DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        await base.DisposeAsync();
        await _broker.DeleteTopologyAsync();
        await _broker.DisposeAsync();
    }

    [Fact]
    public async Task Triggered_alert_is_pushed_live_to_its_owner_only()
    {
        var alice = await RegisteredClientAsync("alice@example.com");
        var bob = await RegisteredClientAsync("bob@example.com");
        var aliceId = await UserIdAsync(alice);

        var aliceReceived = new TaskCompletionSource<NotificationResponse>();
        var bobReceived = new TaskCompletionSource<NotificationResponse>();

        await ConnectAsync(alice, n => aliceReceived.TrySetResult(n));
        await ConnectAsync(bob, n => bobReceived.TrySetResult(n));
        await WaitForApiConsumerAsync();

        var message = new AlertTriggeredMessage
        {
            MessageId = Guid.NewGuid(),
            AlertId = Guid.NewGuid(),
            UserId = aliceId,
            Symbol = "AAPL",
            Direction = AlertDirection.Above,
            TargetPrice = 100m,
            TriggerPrice = 150m,
            TriggeredAt = DateTimeOffset.UtcNow
        };

        await _broker.PublishToQueueAsync(
            QueueTopology.AlertTriggered.Queue,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)));

        var pushed = await aliceReceived.Task.WaitAsync(Timeout);

        Assert.Equal(message.AlertId, pushed.AlertId);
        Assert.Equal(NotificationType.AlertTriggered, pushed.Type);
        Assert.Equal("AAPL alert triggered", pushed.Title);

        // The pushed notification is the stored one.
        var list = await alice.Http.GetFromJsonAsync<NotificationListResponse>("/api/notifications", Json);
        Assert.Equal(pushed.Id, Assert.Single(list!.Items).Id);

        // Give a misrouted push time to arrive before asserting it didn't.
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(bobReceived.Task.IsCompleted);
    }

    [Fact]
    public async Task Hub_rejects_connections_without_the_auth_cookie()
    {
        var connection = BuildConnection(accessToken: null);
        _connections.Add(connection);

        await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());
    }

    private async Task ConnectAsync(ApiClient client, Action<NotificationResponse> onNotification)
    {
        var connection = BuildConnection(client.Cookie(AccessTokenCookie));
        _connections.Add(connection);

        connection.On(NotificationsHub.NotificationReceived, onNotification);

        await connection.StartAsync();
    }

    private HubConnection BuildConnection(string? accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(Factory.Server.BaseAddress, NotificationsHub.Path.TrimStart('/')), options =>
            {
                // In-memory test server: long polling over its handler.
                options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;

                if (accessToken is not null)
                {
                    options.Headers["Cookie"] = $"{AccessTokenCookie}={accessToken}";
                }
            })
            .AddJsonProtocol(options =>
                options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

    private Task WaitForApiConsumerAsync() =>
        Eventually.TrueAsync(
            async () => await _broker.ConsumerCountAsync(QueueTopology.AlertTriggered.Queue) >= 1,
            Timeout,
            "the API's notification consumer to subscribe");

    private static async Task<string> UserIdAsync(ApiClient client) =>
        (await client.Http.GetFromJsonAsync<Me>("/api/auth/me", Json))!.Id;

    private sealed record Me(string Id, string Email);
}
