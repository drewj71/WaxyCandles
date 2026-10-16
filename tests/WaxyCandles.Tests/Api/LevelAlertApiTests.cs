using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WaxyCandles.Application.Alerts.Interfaces;
using WaxyCandles.Application.Alerts.Levels;
using WaxyCandles.Application.Options.Gex;
using WaxyCandles.Tests.Alerts;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Api;

/// <summary>GEX level alerts through /api/alerts, with the GEX board faked.</summary>
public class LevelAlertApiTests(IntegrationFixture fixture) : ApiTestBase(fixture)
{
    private readonly FakeGexLevelBoardSource _boards = new();

    private HttpClient _http = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var factory = Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IGexLevelBoardSource>(_boards)));

        _http = factory.CreateDefaultClient(BaseAddress, new CookieContainerHandler(new CookieContainer()));
        (await RegisterAsync(_http, "levels@example.com")).EnsureSuccessStatusCode();

        var expiration = new DateOnly(2026, 10, 16);
        _boards.Boards["SPY"] = new GexLevelBoard(
            new GexLevels(new GexNode(600m, expiration, -5_000_000), null, null, null),
            DateTimeOffset.Parse("2026-10-15T20:00:00Z"),
            580m);
    }

    [Fact]
    public async Task Creates_a_level_alert_with_enum_names_as_the_web_app_sends_them()
    {
        var response = await _http.PostAsJsonAsync("/api/alerts", new
        {
            symbol = "SPY",
            level = "King",
            levelProximityPercent = 0.5,
            levelDays = 30,
            levelStrikeRange = 10,
            frequency = "EveryCross"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal("King", root.GetProperty("level").GetString());
        Assert.Equal(597m, root.GetProperty("targetPrice").GetDecimal());
        Assert.Equal("Above", root.GetProperty("direction").GetString());
        Assert.Equal(600m, root.GetProperty("levelPrice").GetDecimal());
        Assert.Equal(0.5m, root.GetProperty("levelProximityPercent").GetDecimal());
        Assert.Equal(30, root.GetProperty("levelDays").GetInt32());
        Assert.Equal(10m, root.GetProperty("levelStrikeRange").GetDecimal());
        Assert.Equal(
            DateTimeOffset.Parse("2026-10-15T20:00:00Z"),
            root.GetProperty("levelAsOf").GetDateTimeOffset());
    }

    [Fact]
    public async Task Missing_level_is_a_400_on_the_level_field()
    {
        var response = await _http.PostAsJsonAsync("/api/alerts", new
        {
            symbol = "SPY",
            level = "Gatekeeper"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var message = json.RootElement.GetProperty("errors").GetProperty("level")[0].GetString();
        Assert.StartsWith("SPY has no Gatekeeper", message);
    }

    [Fact]
    public async Task Price_alert_responses_have_null_level_fields()
    {
        var response = await _http.PostAsJsonAsync("/api/alerts", new
        {
            symbol = "SPY",
            targetPrice = 600,
            direction = "Above"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("level").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("levelPrice").ValueKind);
        Assert.Empty(_boards.Requests);
    }
}
