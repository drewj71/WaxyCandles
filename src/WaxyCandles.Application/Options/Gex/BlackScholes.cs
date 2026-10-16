using WaxyCandles.Application.Options.Models;

namespace WaxyCandles.Application.Options.Gex;

/// <summary>
/// Black-Scholes with no dividend yield. Used for vanna (no provider supplies
/// it) and for gamma when the provider's is missing. Volatility and time are
/// annualised: 0.25 is 25% IV, 0.5 is half a year.
/// </summary>
public static class BlackScholes
{
    private const double MinVolatility = 1e-4;
    private const double MaxVolatility = 5.0;

    /// <summary>d(delta)/d(spot): delta change per $1 move in the underlying.</summary>
    public static double Gamma(double spot, double strike, double years, double volatility, double riskFreeRate)
    {
        var (d1, _) = D1D2(spot, strike, years, volatility, riskFreeRate);

        return Pdf(d1) / (spot * volatility * Math.Sqrt(years));
    }

    /// <summary>d(delta)/d(volatility): delta change per 1.00 (100 points) of IV. Same for calls and puts.</summary>
    public static double Vanna(double spot, double strike, double years, double volatility, double riskFreeRate)
    {
        var (d1, d2) = D1D2(spot, strike, years, volatility, riskFreeRate);

        return -Pdf(d1) * d2 / volatility;
    }

    public static double Price(OptionSide side, double spot, double strike, double years, double volatility, double riskFreeRate)
    {
        var (d1, d2) = D1D2(spot, strike, years, volatility, riskFreeRate);
        var discountedStrike = strike * Math.Exp(-riskFreeRate * years);

        return side == OptionSide.Call
            ? spot * Cdf(d1) - discountedStrike * Cdf(d2)
            : discountedStrike * Cdf(-d2) - spot * Cdf(-d1);
    }

    /// <summary>
    /// The volatility at which the model price equals <paramref name="price"/>,
    /// or null when no volatility in 0.01%–500% reproduces it (a price below
    /// intrinsic value, or a stale mark).
    /// </summary>
    public static double? ImpliedVolatility(OptionSide side, double price, double spot, double strike, double years, double riskFreeRate)
    {
        if (price <= 0 || years <= 0)
        {
            return null;
        }

        var low = MinVolatility;
        var high = MaxVolatility;

        // Price rises with volatility, so bisection always converges.
        if (price < Price(side, spot, strike, years, low, riskFreeRate) ||
            price > Price(side, spot, strike, years, high, riskFreeRate))
        {
            return null;
        }

        for (var i = 0; i < 100 && high - low > 1e-7; i++)
        {
            var mid = (low + high) / 2;

            if (Price(side, spot, strike, years, mid, riskFreeRate) < price)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return (low + high) / 2;
    }

    private static (double D1, double D2) D1D2(double spot, double strike, double years, double volatility, double riskFreeRate)
    {
        var volSqrtT = volatility * Math.Sqrt(years);
        var d1 = (Math.Log(spot / strike) + (riskFreeRate + volatility * volatility / 2) * years) / volSqrtT;

        return (d1, d1 - volSqrtT);
    }

    private static double Pdf(double x) =>
        Math.Exp(-x * x / 2) / Math.Sqrt(2 * Math.PI);

    // Abramowitz & Stegun 7.1.26 erf, absolute error below 1.5e-7.
    private static double Cdf(double x)
    {
        var z = Math.Abs(x) / Math.Sqrt(2);
        var t = 1 / (1 + 0.3275911 * z);
        var erf = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592)
            * t * Math.Exp(-z * z);

        return x >= 0 ? (1 + erf) / 2 : (1 - erf) / 2;
    }
}
