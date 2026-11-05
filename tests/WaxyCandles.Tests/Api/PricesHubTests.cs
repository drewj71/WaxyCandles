using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using WaxyCandles.Api.Hubs;
using WaxyCandles.Application.MarketData.LivePrices;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.LivePrices;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Api;

/// <summary>
/// Live prices end to end through the real API host: a client subscribes on
/// the cookie-authenticated PricesHub, the hosted broadcaster finds a newly
/// stored price and pushes it to that symbol's subscribers only.
/// </summary>
public class PricesHubTests(IntegrationFixture fixture) : ApiTestBase(fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private WaxyCandlesApiFactory _factory = default!;
    private readonly List<HubConnection> _connections = [];

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var settings = Settings();
        settings["LivePrices:PollInterval"] = "00:00:00.5";
        settings["LivePrices:MaxSubscriptionsPerConnection"] = "3";
        _factory = new WaxyCandlesApiFactory(settings, MarketData);
    }

    public override async Task DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private LivePriceSubscriptions Subscriptions =>
        _factory.Services.GetRequiredService<LivePriceSubscriptions>();

    [Fact]
    public async Task A_new_price_is_pushed_to_subscribers_of_its_symbol_only()
    {
        var alice = await RegisteredClientAsync("alice@example.com", _factory);

        var aaplWatcher = await ConnectAsync(alice);
        var msftWatcher = await ConnectAsync(alice);

        var aaplPrices = Collect(aaplWatcher);
        var msftPrices = Collect(msftWatcher);

        await aaplWatcher.InvokeAsync(nameof(PricesHub.Subscribe), " aapl ");
        await msftWatcher.InvokeAsync(nameof(PricesHub.Subscribe), "MSFT");

        var at = DateTimeOffset.UtcNow;
        await SeedPriceAsync("AAPL", 150m, at, change: 3m);

        await Eventually.TrueAsync(() => !aaplPrices.IsEmpty, Timeout, "the AAPL price to be pushed");

        Assert.True(aaplPrices.TryPeek(out var pushed));
        Assert.Equal("AAPL", pushed.Symbol);
        Assert.Equal(150m, pushed.Price);
        Assert.Equal(147m, pushed.PreviousClose);
        Assert.Equal(at, pushed.Timestamp);

        // A newer price is pushed too; the same one is not pushed again.
        await SeedPriceAsync("AAPL", 151m, at.AddMinutes(1));
        await Eventually.TrueAsync(() => aaplPrices.Count == 2, Timeout, "the newer AAPL price");

        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Equal(2, aaplPrices.Count);
        Assert.True(msftPrices.IsEmpty);
    }

    [Fact]
    public async Task Unsubscribed_symbols_are_no_longer_pushed()
    {
        var alice = await RegisteredClientAsync("alice@example.com", _factory);
        var connection = await ConnectAsync(alice);
        var prices = Collect(connection);

        await connection.InvokeAsync(nameof(PricesHub.Subscribe), "AAPL");
        await connection.InvokeAsync(nameof(PricesHub.Unsubscribe), "aapl");
        Assert.Empty(Subscriptions.Symbols());

        await SeedPriceAsync("AAPL", 150m, DateTimeOffset.UtcNow);
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        Assert.True(prices.IsEmpty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NOT A SYMBOL")]
    [InlineData("WAYTOOLONGSYMBOL")]
    [InlineData("AAPL;DROP")]
    public async Task Invalid_symbols_are_rejected(string symbol)
    {
        var alice = await RegisteredClientAsync("alice@example.com", _factory);
        var connection = await ConnectAsync(alice);

        var error = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync(nameof(PricesHub.Subscribe), symbol));

        Assert.Contains("Symbol must be", error.Message);
        Assert.Empty(Subscriptions.Symbols());
    }

    [Fact]
    public async Task A_connection_can_watch_at_most_the_configured_number_of_symbols()
    {
        var alice = await RegisteredClientAsync("alice@example.com", _factory);
        var connection = await ConnectAsync(alice);

        foreach (var symbol in new[] { "A", "B", "C" })
        {
            await connection.InvokeAsync(nameof(PricesHub.Subscribe), symbol);
        }

        var error = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync(nameof(PricesHub.Subscribe), "D"));
        Assert.Contains("at most 3", error.Message);

        // Re-subscribing to one it has is fine.
        await connection.InvokeAsync(nameof(PricesHub.Subscribe), "A");

        Assert.Equal(["A", "B", "C"], Subscriptions.Symbols().Order());
    }

    [Fact]
    public async Task Subscriptions_are_removed_when_the_connection_closes()
    {
        var alice = await RegisteredClientAsync("alice@example.com", _factory);
        var leaving = await ConnectAsync(alice);
        var staying = await ConnectAsync(alice);

        await leaving.InvokeAsync(nameof(PricesHub.Subscribe), "AAPL");
        await leaving.InvokeAsync(nameof(PricesHub.Subscribe), "MSFT");
        await staying.InvokeAsync(nameof(PricesHub.Subscribe), "MSFT");

        Assert.Equal(2, Subscriptions.ConnectionCount);

        await leaving.StopAsync();

        await Eventually.TrueAsync(
            () => Subscriptions.ConnectionCount == 1,
            Timeout,
            "the closed connection's subscriptions to be removed");

        Assert.Equal(["MSFT"], Subscriptions.Symbols());
    }

    [Fact]
    public async Task Hub_rejects_connections_without_the_auth_cookie()
    {
        var connection = BuildConnection(accessToken: null);
        _connections.Add(connection);

        await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());
    }

    private async Task<ApiClient> RegisteredClientAsync(string email, WaxyCandlesApiFactory factory)
    {
        var client = NewClient(factory);
        (await RegisterAsync(client.Http, email)).EnsureSuccessStatusCode();
        return client;
    }

    private async Task<HubConnection> ConnectAsync(ApiClient client)
    {
        var connection = BuildConnection(client.Cookie(AccessTokenCookie));
        _connections.Add(connection);
        await connection.StartAsync();
        return connection;
    }

    private static System.Collections.Concurrent.ConcurrentQueue<LivePriceUpdate> Collect(HubConnection connection)
    {
        var received = new System.Collections.Concurrent.ConcurrentQueue<LivePriceUpdate>();
        connection.On<LivePriceUpdate>(PricesHub.PriceUpdated, received.Enqueue);
        return received;
    }

    private HubConnection BuildConnection(string? accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, PricesHub.Path.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;

                if (accessToken is not null)
                {
                    options.Headers["Cookie"] = $"{AccessTokenCookie}={accessToken}";
                }
            })
            .AddJsonProtocol(options =>
                options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

    private async Task SeedPriceAsync(string symbol, decimal price, DateTimeOffset at, decimal change = 0m)
    {
        await using var db = NewDbContext();
        db.StockPrices.Add(new StockPrice
        {
            Id = Guid.NewGuid(),
            Symbol = symbol,
            Price = price,
            Change = change,
            ChangePercent = 0m,
            Timestamp = at
        });
        await db.SaveChangesAsync();
    }
}
