using WaxyCandles.Application.Options.Gex;
using WaxyCandles.Application.Options.Models;

namespace WaxyCandles.Tests.Options;

/// <summary>
/// Textbook case S = K = 100, one year, 20% vol, 5% rate:
/// d1 = 0.35, d2 = 0.15, call 10.4506, put 5.5735.
/// </summary>
public class BlackScholesTests
{
    private const double Spot = 100;
    private const double Strike = 100;
    private const double Years = 1;
    private const double Vol = 0.2;
    private const double Rate = 0.05;

    [Fact]
    public void Prices_match_the_textbook_values()
    {
        Assert.Equal(10.4506, BlackScholes.Price(OptionSide.Call, Spot, Strike, Years, Vol, Rate), 3);
        Assert.Equal(5.5735, BlackScholes.Price(OptionSide.Put, Spot, Strike, Years, Vol, Rate), 3);
    }

    [Fact]
    public void Gamma_and_vanna_match_the_closed_form()
    {
        // phi(0.35) = 0.375240
        Assert.Equal(0.375240 / (100 * 0.2), BlackScholes.Gamma(Spot, Strike, Years, Vol, Rate), 5);
        Assert.Equal(-0.375240 * 0.15 / 0.2, BlackScholes.Vanna(Spot, Strike, Years, Vol, Rate), 5);
    }

    [Theory]
    [InlineData(OptionSide.Call, 90.0)]
    [InlineData(OptionSide.Call, 110.0)]
    [InlineData(OptionSide.Put, 90.0)]
    [InlineData(OptionSide.Put, 110.0)]
    public void Implied_volatility_recovers_the_volatility_a_price_came_from(OptionSide side, double strike)
    {
        var price = BlackScholes.Price(side, Spot, strike, 0.25, 0.35, Rate);

        var iv = BlackScholes.ImpliedVolatility(side, price, Spot, strike, 0.25, Rate);

        Assert.NotNull(iv);
        Assert.Equal(0.35, iv.Value, 4);
    }

    [Theory]
    // Below intrinsic value (a call 20 in the money for 5): no volatility fits.
    [InlineData(OptionSide.Call, 80.0, 5.0)]
    // More than the underlying itself.
    [InlineData(OptionSide.Call, 100.0, 150.0)]
    [InlineData(OptionSide.Put, 100.0, 0.0)]
    public void Implied_volatility_is_null_for_impossible_prices(OptionSide side, double strike, double price)
    {
        Assert.Null(BlackScholes.ImpliedVolatility(side, price, Spot, strike, 0.25, Rate));
    }
}
