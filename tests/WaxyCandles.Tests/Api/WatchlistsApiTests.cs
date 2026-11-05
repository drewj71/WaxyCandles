using System.Net;
using System.Net.Http.Json;
using WaxyCandles.Application.Watchlists.Models;
using WaxyCandles.Application.Watchlists.Validation;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Api;

/// <summary>
/// The named-watchlist endpoints (/api/watchlists) through the real API: CRUD,
/// ordering, limits as 400s, another user's list as 404, and the single-list
/// endpoints (/api/watchlist) still acting on the default list.
/// </summary>
public class WatchlistsApiTests(IntegrationFixture fixture) : ApiTestBase(fixture)
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
    public async Task Lists_can_be_created_filled_reordered_renamed_and_deleted()
    {
        var tech = await CreateAsync(_alice, "Tech");
        var energy = await CreateAsync(_alice, "Energy");

        await AddSymbolAsync(_alice, tech.Id, "aapl");
        await AddSymbolAsync(_alice, tech.Id, "MSFT");
        var withNvda = await AddSymbolAsync(_alice, tech.Id, "NVDA");
        Assert.Equal(["AAPL", "MSFT", "NVDA"], withNvda.Symbols);

        var reordered = await _alice.Http.PutAsJsonAsync(
            $"/api/watchlists/{tech.Id}/symbols/order",
            new { symbols = new[] { "NVDA", "AAPL", "MSFT" } },
            Json);
        reordered.EnsureSuccessStatusCode();

        (await _alice.Http.PutAsJsonAsync(
            "/api/watchlists/order",
            new { ids = new[] { energy.Id, tech.Id } },
            Json)).EnsureSuccessStatusCode();

        var renamed = await _alice.Http.PutAsJsonAsync($"/api/watchlists/{tech.Id}", new { name = "Semis" }, Json);
        Assert.Equal("Semis", (await renamed.Content.ReadFromJsonAsync<WatchlistDto>(Json))!.Name);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await _alice.Http.DeleteAsync($"/api/watchlists/{tech.Id}/symbols/aapl")).StatusCode);

        var lists = await ListsAsync(_alice);
        Assert.Equal(["Energy", "Semis"], lists.Select(x => x.Name));
        Assert.Equal(["NVDA", "MSFT"], lists[1].Symbols);

        Assert.Equal(HttpStatusCode.NoContent, (await _alice.Http.DeleteAsync($"/api/watchlists/{energy.Id}")).StatusCode);
        Assert.Equal(["Semis"], (await ListsAsync(_alice)).Select(x => x.Name));
    }

    [Fact]
    public async Task Create_returns_201_with_a_location()
    {
        var response = await _alice.Http.PostAsJsonAsync("/api/watchlists", new { name = "Tech" }, Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<WatchlistDto>(Json);

        var fetched = await _alice.Http.GetFromJsonAsync<WatchlistDto>(response.Headers.Location, Json);
        Assert.Equal(created!.Id, fetched!.Id);
    }

    [Fact]
    public async Task Another_users_list_is_not_found_for_every_endpoint()
    {
        var list = await CreateAsync(_alice, "Alice's");
        await AddSymbolAsync(_alice, list.Id, "AAPL");

        var attempts = new Func<Task<HttpResponseMessage>>[]
        {
            () => _bob.Http.GetAsync($"/api/watchlists/{list.Id}"),
            () => _bob.Http.GetAsync($"/api/watchlists/{list.Id}/quotes"),
            () => _bob.Http.PutAsJsonAsync($"/api/watchlists/{list.Id}", new { name = "Bob's" }, Json),
            () => _bob.Http.DeleteAsync($"/api/watchlists/{list.Id}"),
            () => _bob.Http.PostAsJsonAsync($"/api/watchlists/{list.Id}/symbols", new { symbol = "MSFT" }, Json),
            () => _bob.Http.DeleteAsync($"/api/watchlists/{list.Id}/symbols/AAPL"),
            () => _bob.Http.PutAsJsonAsync(
                $"/api/watchlists/{list.Id}/symbols/order", new { symbols = new[] { "AAPL" } }, Json)
        };

        foreach (var attempt in attempts)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await attempt()).StatusCode);
        }

        // Reordering with someone else's id is invalid input, not a reorder.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await _bob.Http.PutAsJsonAsync("/api/watchlists/order", new { ids = new[] { list.Id } }, Json)).StatusCode);

        Assert.Empty(await ListsAsync(_bob));

        var unchanged = Assert.Single(await ListsAsync(_alice));
        Assert.Equal("Alice's", unchanged.Name);
        Assert.Equal(["AAPL"], unchanged.Symbols);
    }

    [Fact]
    public async Task Limits_and_invalid_input_are_400s_naming_the_field()
    {
        var list = await CreateAsync(_alice, "Tech");

        await AssertBadRequestAsync(
            await _alice.Http.PostAsJsonAsync("/api/watchlists", new { name = "  " }, Json), "name");
        await AssertBadRequestAsync(
            await _alice.Http.PostAsJsonAsync("/api/watchlists", new { name = "tech" }, Json), "name");
        await AssertBadRequestAsync(
            await _alice.Http.PostAsJsonAsync($"/api/watchlists/{list.Id}/symbols", new { symbol = "BAD SYMBOL!" }, Json),
            "symbol");

        for (var i = 0; i < WatchlistRules.MaxSymbolsPerList; i++)
        {
            await AddSymbolAsync(_alice, list.Id, $"S{i}");
        }

        await AssertBadRequestAsync(
            await _alice.Http.PostAsJsonAsync($"/api/watchlists/{list.Id}/symbols", new { symbol = "EXTRA" }, Json),
            "symbol");

        for (var i = 1; i < WatchlistRules.MaxListsPerUser; i++)
        {
            await CreateAsync(_alice, $"List {i}");
        }

        await AssertBadRequestAsync(
            await _alice.Http.PostAsJsonAsync("/api/watchlists", new { name = "One too many" }, Json), "name");
    }

    [Fact]
    public async Task Single_list_endpoints_act_on_the_first_list()
    {
        // The first add creates the default list.
        (await _alice.Http.PostAsync("/api/watchlist/AAPL", null)).EnsureSuccessStatusCode();

        var list = Assert.Single(await ListsAsync(_alice));
        Assert.Equal(WatchlistRules.DefaultListName, list.Name);
        Assert.Equal(["AAPL"], list.Symbols);

        // A second list placed first becomes the one /api/watchlist shows.
        var other = await CreateAsync(_alice, "Other");
        await AddSymbolAsync(_alice, other.Id, "MSFT");
        (await _alice.Http.PutAsJsonAsync("/api/watchlists/order", new { ids = new[] { other.Id, list.Id } }, Json))
            .EnsureSuccessStatusCode();

        Assert.Equal(["MSFT"], await _alice.Http.GetFromJsonAsync<List<string>>("/api/watchlist", Json));

        var quotes = await _alice.Http.GetFromJsonAsync<List<WatchlistStockDto>>("/api/watchlist/quotes", Json);
        Assert.Equal(["MSFT"], quotes!.Select(x => x.Symbol));
    }

    [Fact]
    public async Task Quotes_endpoint_lists_every_symbol_in_list_order()
    {
        var list = await CreateAsync(_alice, "Tech");
        await AddSymbolAsync(_alice, list.Id, "MSFT");
        await AddSymbolAsync(_alice, list.Id, "AAPL");

        await using (var db = NewDbContext())
        {
            db.StockPrices.Add(new StockPrice
            {
                Id = Guid.NewGuid(),
                Symbol = "AAPL",
                Price = 150m,
                Change = 3m,
                ChangePercent = 2.04m,
                Timestamp = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var quotes = (await _alice.Http.GetFromJsonAsync<List<WatchlistQuoteDto>>(
            $"/api/watchlists/{list.Id}/quotes", Json))!;

        Assert.Equal(["MSFT", "AAPL"], quotes.Select(x => x.Symbol));
        Assert.Null(quotes[0].Price);
        Assert.Equal(150m, quotes[1].Price);
        Assert.Equal(147m, quotes[1].PreviousClose);
    }

    private static async Task<WatchlistDto> CreateAsync(ApiClient client, string name)
    {
        var response = await client.Http.PostAsJsonAsync("/api/watchlists", new { name }, Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WatchlistDto>(Json))!;
    }

    private static async Task<WatchlistDto> AddSymbolAsync(ApiClient client, Guid listId, string symbol)
    {
        var response = await client.Http.PostAsJsonAsync($"/api/watchlists/{listId}/symbols", new { symbol }, Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WatchlistDto>(Json))!;
    }

    private static async Task<List<WatchlistDto>> ListsAsync(ApiClient client) =>
        (await client.Http.GetFromJsonAsync<List<WatchlistDto>>("/api/watchlists", Json))!;

    private static async Task AssertBadRequestAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ValidationBody>(Json);
        Assert.Contains(field, body!.Errors.Keys);
    }

    private sealed record ValidationBody(string Message, Dictionary<string, string[]> Errors);
}
