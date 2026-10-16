using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WaxyCandles.Application.Options.Interfaces;
using WaxyCandles.Application.Options.Models;
using WaxyCandles.Tests.Infrastructure;
using WaxyCandles.Tests.Options;

namespace WaxyCandles.Tests.Api;

/// <summary>
/// GET /api/stocks/{symbol}/gex through the real API, with the quote and option
/// chain providers faked. Pins the JSON shape the web app reads.
/// </summary>
public class GexApiTests(IntegrationFixture fixture) : ApiTestBase(fixture)
{
    private readonly FakeOptionsDataProvider _options = new();

    private HttpClient _http = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var factory = Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IOptionsDataProvider>(_options)));

        _http = factory.CreateDefaultClient(BaseAddress, new CookieContainerHandler(new CookieContainer()));
        (await RegisterAsync(_http, "gex@example.com")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Returns_the_table_in_the_shape_the_web_app_reads()
    {
        var now = DateTimeOffset.UtcNow;
        var expiration = now.AddDays(3);

        MarketData.SetQuote("AAPL", 100m);
        _options.Chain = new OptionChain("AAPL", 100m, now,
        [
            new("C100", OptionSide.Call, 100m, expiration, 1000, 0.3, 0.05, 2m),
            new("P95", OptionSide.Put, 95m, expiration, 4000, 0.3, 0.04, 1m)
        ]);

        var response = await _http.GetAsync("/api/stocks/aapl/gex?days=7&strikeRange=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal("AAPL", root.GetProperty("symbol").GetString());
        Assert.Equal(100m, root.GetProperty("underlyingPrice").GetDecimal());
        // DateOnly serializes as yyyy-MM-dd, which the page parses as a calendar date.
        var expirationText = root.GetProperty("expirations")[0].GetString();
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", expirationText);

        var rows = root.GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal(100m, rows[0].GetProperty("strike").GetDecimal());
        Assert.Equal(500_000, rows[0].GetProperty("gex")[0].GetDouble(), 6);
        Assert.Equal(JsonValueKind.Number, rows[0].GetProperty("vex")[0].ValueKind);
        Assert.Equal(500_000, rows[0].GetProperty("netGex").GetDouble(), 6);

        var king = root.GetProperty("gexLevels").GetProperty("king");
        Assert.Equal(95m, king.GetProperty("strike").GetDecimal());
        Assert.Equal(expirationText, king.GetProperty("expiration").GetString());
        Assert.True(king.GetProperty("value").GetDouble() < 0);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("gexLevels").GetProperty("gatekeeper").ValueKind);

        Assert.Equal(2, root.GetProperty("contractCount").GetInt32());
        Assert.Equal(0, root.GetProperty("skippedContractCount").GetInt32());
        Assert.True(root.TryGetProperty("asOf", out _));
        Assert.True(root.TryGetProperty("totalVex", out _));
    }

    [Fact]
    public async Task Symbol_without_data_is_404_with_a_message()
    {
        var response = await _http.GetAsync("/api/stocks/nope/gex");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("No option data for NOPE.", json.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Out_of_range_parameters_are_400_with_field_errors()
    {
        var response = await _http.GetAsync("/api/stocks/AAPL/gex?days=999&strikeRange=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = json.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("days", out _));
        Assert.True(errors.TryGetProperty("strikeRange", out _));
        Assert.Empty(_options.Queries);
    }

    [Fact]
    public async Task Requires_sign_in()
    {
        var response = await NewRawClient().GetAsync("/api/stocks/AAPL/gex");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
