using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.Alerts.DTOs;
using WaxyCandles.Application.Watchlists.Models;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Api;

/// <summary>
/// Every alert and watchlist endpoint scopes by the caller's user id from the JWT.
/// One user must never read or change another user's data.
/// </summary>
public class CrossUserAuthorizationTests(IntegrationFixture fixture) : ApiTestBase(fixture)
{
    private ApiClient _alice = default!;
    private ApiClient _bob = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _alice = await RegisteredClientAsync("alice@example.com");
        _bob = await RegisteredClientAsync("bob@example.com");
    }

    [Fact]
    public async Task Created_alert_belongs_to_its_creator()
    {
        var created = await CreateAlertAsync(_alice, "aapl");

        Assert.Equal("AAPL", created.Symbol);

        var aliceAlerts = await _alice.Http.GetFromJsonAsync<List<AlertResponse>>("/api/alerts", Json);
        var bobAlerts = await _bob.Http.GetFromJsonAsync<List<AlertResponse>>("/api/alerts", Json);

        Assert.Equal([created.Id], aliceAlerts!.Select(a => a.Id));
        Assert.Empty(bobAlerts!);
    }

    [Fact]
    public async Task Another_users_alert_cannot_be_read()
    {
        var alert = await CreateAlertAsync(_alice, "AAPL");

        var response = await _bob.Http.GetAsync($"/api/alerts/{alert.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Another_users_alert_cannot_be_updated()
    {
        var alert = await CreateAlertAsync(_alice, "AAPL", targetPrice: 100m);

        var response = await _bob.Http.PutAsJsonAsync(
            $"/api/alerts/{alert.Id}",
            new UpdateAlertRequest { TargetPrice = 1m, Direction = AlertDirection.Below },
            Json);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var stored = await LoadAlertAsync(alert.Id);
        Assert.Equal(100m, stored.TargetPrice);
        Assert.Equal(AlertDirection.Above, stored.Direction);
    }

    [Fact]
    public async Task Another_users_alert_cannot_be_deleted()
    {
        var alert = await CreateAlertAsync(_alice, "AAPL");

        // Returns 204 either way, so it doesn't reveal whether the id exists.
        var response = await _bob.Http.DeleteAsync($"/api/alerts/{alert.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull(await LoadAlertAsync(alert.Id));
    }

    [Fact]
    public async Task Owner_can_update_and_delete_their_alert()
    {
        var alert = await CreateAlertAsync(_alice, "AAPL", targetPrice: 100m);

        var update = await _alice.Http.PutAsJsonAsync(
            $"/api/alerts/{alert.Id}",
            new UpdateAlertRequest { TargetPrice = 90m, Direction = AlertDirection.Below },
            Json);

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(90m, (await LoadAlertAsync(alert.Id)).TargetPrice);

        var delete = await _alice.Http.DeleteAsync($"/api/alerts/{alert.Id}");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await _alice.Http.GetAsync($"/api/alerts/{alert.Id}")).StatusCode);
    }

    [Fact]
    public async Task Watchlists_are_private_to_each_user()
    {
        (await _alice.Http.PostAsync("/api/watchlist/AAPL", null)).EnsureSuccessStatusCode();

        Assert.Equal(["AAPL"], await GetWatchlistAsync(_alice));
        Assert.Empty(await GetWatchlistAsync(_bob));
    }

    [Fact]
    public async Task Removing_a_symbol_only_affects_the_callers_watchlist()
    {
        (await _alice.Http.PostAsync("/api/watchlist/AAPL", null)).EnsureSuccessStatusCode();
        (await _bob.Http.PostAsync("/api/watchlist/AAPL", null)).EnsureSuccessStatusCode();

        var response = await _bob.Http.DeleteAsync("/api/watchlist/AAPL");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["AAPL"], await GetWatchlistAsync(_alice));
        Assert.Empty(await GetWatchlistAsync(_bob));
    }

    [Fact]
    public async Task Watchlist_quotes_only_include_the_callers_symbols()
    {
        (await _alice.Http.PostAsync("/api/watchlist/AAPL", null)).EnsureSuccessStatusCode();
        (await _bob.Http.PostAsync("/api/watchlist/MSFT", null)).EnsureSuccessStatusCode();

        await using (var db = NewDbContext())
        {
            db.StockPrices.AddRange(
                new StockPrice { Id = Guid.NewGuid(), Symbol = "AAPL", Price = 150m },
                new StockPrice { Id = Guid.NewGuid(), Symbol = "MSFT", Price = 300m });
            await db.SaveChangesAsync();
        }

        var quotes = await _alice.Http.GetFromJsonAsync<List<WatchlistStockDto>>(
            "/api/watchlist/quotes",
            Json);

        var quote = Assert.Single(quotes!);
        Assert.Equal("AAPL", quote.Symbol);
        Assert.Equal(150m, quote.Price);
    }

    private static async Task<AlertResponse> CreateAlertAsync(
        ApiClient client,
        string symbol,
        decimal targetPrice = 100m)
    {
        var response = await client.Http.PostAsJsonAsync(
            "/api/alerts",
            new CreateAlertRequest
            {
                Symbol = symbol,
                TargetPrice = targetPrice,
                Direction = AlertDirection.Above
            },
            Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<AlertResponse>(Json))!;
    }

    private static async Task<List<string>> GetWatchlistAsync(ApiClient client) =>
        (await client.Http.GetFromJsonAsync<List<string>>("/api/watchlist", Json))!;

    private async Task<Alert> LoadAlertAsync(Guid id)
    {
        await using var db = NewDbContext();
        return await db.Alerts.SingleAsync(a => a.Id == id);
    }
}
