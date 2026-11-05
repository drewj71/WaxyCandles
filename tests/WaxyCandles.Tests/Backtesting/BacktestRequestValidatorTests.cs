using WaxyCandles.Application.Backtesting;
using WaxyCandles.Application.Backtesting.Strategies;
using WaxyCandles.Application.Common.Exceptions;

namespace WaxyCandles.Tests.Backtesting;

public class BacktestRequestValidatorTests
{
    private static readonly DateOnly Today = new(2026, 11, 4);

    private static BacktestRequest Valid() => new()
    {
        Symbol = " aapl ",
        From = new DateOnly(2020, 1, 1),
        To = new DateOnly(2024, 12, 31)
    };

    private static IReadOnlyDictionary<string, string[]> Errors(BacktestRequest request) =>
        Assert.Throws<RequestValidationException>(() => BacktestRequestValidator.Validate(request, Today)).Errors;

    [Fact]
    public void Defaults_are_valid_and_the_symbol_is_normalized()
    {
        var backtest = BacktestRequestValidator.Validate(Valid(), Today);

        Assert.Equal("AAPL", backtest.Symbol);
        Assert.Equal(BacktestStrategyType.MovingAverageCrossover, backtest.StrategyType);
        Assert.Equal(200, backtest.Strategy.WarmupBars);
        Assert.Equal(new BacktestCosts(10_000m), backtest.Costs);
    }

    [Fact]
    public void Each_strategy_gets_its_own_parameters()
    {
        var request = Valid();
        request.Strategy = BacktestStrategyType.Breakout;
        request.Breakout = new BreakoutParameters { EntryPeriod = 55, ExitPeriod = 20 };
        // Another strategy's parameters are ignored, even if invalid.
        request.RsiMeanReversion = new RsiMeanReversionParameters { Period = 0 };

        var backtest = BacktestRequestValidator.Validate(request, Today);

        Assert.IsType<DonchianBreakoutStrategy>(backtest.Strategy);
        Assert.Equal(55, backtest.Strategy.WarmupBars);
    }

    [Fact]
    public void A_future_end_date_is_capped_at_today()
    {
        var request = Valid();
        request.To = Today.AddDays(30);

        Assert.Equal(Today, BacktestRequestValidator.Validate(request, Today).To);
    }

    [Fact]
    public void Missing_dates_are_reported_separately()
    {
        var errors = Errors(new BacktestRequest { Symbol = "AAPL" });

        Assert.Equal(["Enter a start date."], errors["from"]);
        Assert.Equal(["Enter an end date."], errors["to"]);
    }

    [Fact]
    public void The_start_must_be_before_the_end()
    {
        var request = Valid();
        request.From = request.To;

        Assert.Equal(["The start date must be before the end date."], Errors(request)["from"]);
    }

    [Fact]
    public void A_future_start_is_rejected()
    {
        var request = Valid();
        request.From = Today.AddDays(1);
        request.To = Today.AddDays(10);

        Assert.Equal(["The start date can't be in the future."], Errors(request)["from"]);
    }

    [Fact]
    public void The_range_is_at_most_20_years()
    {
        var request = Valid();
        request.To = new DateOnly(2024, 1, 1);
        request.From = new DateOnly(2004, 1, 1);
        BacktestRequestValidator.Validate(request, Today);

        request.From = new DateOnly(2003, 12, 31);
        Assert.Equal(["The date range can be at most 20 years."], Errors(request)["from"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1ABC")]
    [InlineData("TOOLONGSYMBOL")]
    [InlineData("AA PL")]
    public void Bad_symbols_are_rejected(string symbol)
    {
        var request = Valid();
        request.Symbol = symbol;

        Assert.True(Errors(request).ContainsKey("symbol"));
    }

    [Theory]
    [InlineData(0, 200, "movingAverageCrossover.fastPeriod")]
    [InlineData(50, 401, "movingAverageCrossover.slowPeriod")]
    [InlineData(50, 50, "movingAverageCrossover.slowPeriod")]
    [InlineData(60, 50, "movingAverageCrossover.slowPeriod")]
    public void Ma_periods_are_validated(int fast, int slow, string field)
    {
        var request = Valid();
        request.MovingAverageCrossover = new MovingAverageCrossoverParameters { FastPeriod = fast, SlowPeriod = slow };

        Assert.True(Errors(request).ContainsKey(field));
    }

    [Theory]
    [InlineData(1, 30, 70, "rsiMeanReversion.period")]
    [InlineData(101, 30, 70, "rsiMeanReversion.period")]
    [InlineData(14, 0, 70, "rsiMeanReversion.oversold")]
    [InlineData(14, 30, 100, "rsiMeanReversion.overbought")]
    [InlineData(14, 70, 30, "rsiMeanReversion.overbought")]
    [InlineData(14, 50, 50, "rsiMeanReversion.overbought")]
    public void Rsi_parameters_are_validated(int period, int oversold, int overbought, string field)
    {
        var request = Valid();
        request.Strategy = BacktestStrategyType.RsiMeanReversion;
        request.RsiMeanReversion = new RsiMeanReversionParameters
        {
            Period = period,
            Oversold = oversold,
            Overbought = overbought
        };

        Assert.True(Errors(request).ContainsKey(field));
    }

    [Theory]
    [InlineData(0, 10, "breakout.entryPeriod")]
    [InlineData(20, 251, "breakout.exitPeriod")]
    public void Breakout_periods_are_validated(int entry, int exit, string field)
    {
        var request = Valid();
        request.Strategy = BacktestStrategyType.Breakout;
        request.Breakout = new BreakoutParameters { EntryPeriod = entry, ExitPeriod = exit };

        Assert.True(Errors(request).ContainsKey(field));
    }

    [Fact]
    public void Costs_are_validated_and_every_problem_is_listed()
    {
        var request = Valid();
        request.InitialCapital = 50m;
        request.CommissionPerTrade = -1m;
        request.CommissionPercent = 6m;
        request.SlippageBps = 501m;

        var errors = Errors(request);

        Assert.Equal(
            ["commissionPercent", "commissionPerTrade", "initialCapital", "slippageBps"],
            errors.Keys.Order());
    }
}
