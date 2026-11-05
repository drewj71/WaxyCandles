using System.Text.RegularExpressions;
using WaxyCandles.Application.Backtesting.Strategies;
using WaxyCandles.Application.Common.Exceptions;

namespace WaxyCandles.Application.Backtesting;

/// <summary>A request that passed validation, with <see cref="To"/> capped at today.</summary>
public sealed record ValidatedBacktest(
    string Symbol,
    DateOnly From,
    DateOnly To,
    BacktestStrategyType StrategyType,
    IBacktestStrategy Strategy,
    BacktestCosts Costs);

public static partial class BacktestRequestValidator
{
    public const int MaxYears = 20;

    public const int MaxMovingAveragePeriod = 400;
    public const int MaxRsiPeriod = 100;
    public const int MaxChannelPeriod = 250;

    public const decimal MinInitialCapital = 100m;
    public const decimal MaxInitialCapital = 1_000_000_000m;
    public const decimal MaxCommissionPerTrade = 1_000m;
    public const decimal MaxCommissionPercent = 5m;
    public const decimal MaxSlippageBps = 500m;

    // Letters first, then letters, digits, '.' or '-' (BRK.B, BF-B), as for GEX.
    [GeneratedRegex("^[A-Z][A-Z0-9.-]{0,9}$")]
    private static partial Regex SymbolPattern();

    /// <param name="today">Today's US/Eastern date; a later <c>To</c> is capped to it.</param>
    /// <exception cref="RequestValidationException">Anything is invalid; every problem is listed.</exception>
    public static ValidatedBacktest Validate(BacktestRequest request, DateOnly today)
    {
        var errors = new Dictionary<string, string[]>();
        var symbol = (request.Symbol ?? "").Trim().ToUpperInvariant();

        if (!SymbolPattern().IsMatch(symbol))
        {
            errors["symbol"] = ["Enter a ticker symbol such as AAPL."];
        }

        var to = request.To is { } requestedTo && requestedTo < today ? requestedTo : today;

        var from = request.From ?? default;

        if (request.From is null || request.To is null)
        {
            if (request.From is null)
            {
                errors["from"] = ["Enter a start date."];
            }

            if (request.To is null)
            {
                errors["to"] = ["Enter an end date."];
            }
        }
        else if (from >= to)
        {
            errors["from"] = [from > today
                ? "The start date can't be in the future."
                : "The start date must be before the end date."];
        }
        else if (from < to.AddYears(-MaxYears))
        {
            errors["from"] = [$"The date range can be at most {MaxYears} years."];
        }

        var strategy = request.Strategy switch
        {
            BacktestStrategyType.MovingAverageCrossover =>
                MovingAverageCrossover(request.MovingAverageCrossover ?? new(), errors),
            BacktestStrategyType.RsiMeanReversion =>
                RsiMeanReversion(request.RsiMeanReversion ?? new(), errors),
            BacktestStrategyType.Breakout =>
                Breakout(request.Breakout ?? new(), errors),
            _ => Invalid(errors, "strategy", "Choose a strategy.")
        };

        Range(errors, "initialCapital", request.InitialCapital, MinInitialCapital, MaxInitialCapital,
            $"Initial capital must be between {MinInitialCapital:N0} and {MaxInitialCapital:N0}.");
        Range(errors, "commissionPerTrade", request.CommissionPerTrade, 0m, MaxCommissionPerTrade,
            $"Commission per trade must be between 0 and {MaxCommissionPerTrade:N0}.");
        Range(errors, "commissionPercent", request.CommissionPercent, 0m, MaxCommissionPercent,
            $"Commission must be between 0 and {MaxCommissionPercent}%.");
        Range(errors, "slippageBps", request.SlippageBps, 0m, MaxSlippageBps,
            $"Slippage must be between 0 and {MaxSlippageBps} basis points.");

        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }

        return new ValidatedBacktest(
            symbol,
            from,
            to,
            request.Strategy,
            strategy!,
            new BacktestCosts(
                request.InitialCapital,
                request.CommissionPerTrade,
                request.CommissionPercent,
                request.SlippageBps));
    }

    private static IBacktestStrategy? MovingAverageCrossover(
        MovingAverageCrossoverParameters parameters,
        Dictionary<string, string[]> errors)
    {
        const string prefix = "movingAverageCrossover";
        var valid = true;

        if (parameters.FastPeriod is < 1 or > MaxMovingAveragePeriod - 1)
        {
            errors[$"{prefix}.fastPeriod"] = [$"The fast period must be between 1 and {MaxMovingAveragePeriod - 1}."];
            valid = false;
        }

        if (parameters.SlowPeriod is < 2 or > MaxMovingAveragePeriod)
        {
            errors[$"{prefix}.slowPeriod"] = [$"The slow period must be between 2 and {MaxMovingAveragePeriod}."];
            valid = false;
        }
        else if (valid && parameters.FastPeriod >= parameters.SlowPeriod)
        {
            errors[$"{prefix}.slowPeriod"] = ["The slow period must be longer than the fast period."];
            valid = false;
        }

        if (!Enum.IsDefined(parameters.MovingAverageType))
        {
            errors[$"{prefix}.movingAverageType"] = ["Choose SMA or EMA."];
            valid = false;
        }

        return valid
            ? new MovingAverageCrossoverStrategy(parameters.FastPeriod, parameters.SlowPeriod, parameters.MovingAverageType)
            : null;
    }

    private static IBacktestStrategy? RsiMeanReversion(
        RsiMeanReversionParameters parameters,
        Dictionary<string, string[]> errors)
    {
        const string prefix = "rsiMeanReversion";
        var valid = true;

        if (parameters.Period is < 2 or > MaxRsiPeriod)
        {
            errors[$"{prefix}.period"] = [$"The RSI period must be between 2 and {MaxRsiPeriod}."];
            valid = false;
        }

        if (parameters.Oversold is <= 0m or >= 100m)
        {
            errors[$"{prefix}.oversold"] = ["The oversold level must be between 0 and 100."];
            valid = false;
        }

        if (parameters.Overbought is <= 0m or >= 100m)
        {
            errors[$"{prefix}.overbought"] = ["The overbought level must be between 0 and 100."];
            valid = false;
        }
        else if (valid && parameters.Oversold >= parameters.Overbought)
        {
            errors[$"{prefix}.overbought"] = ["The overbought level must be above the oversold level."];
            valid = false;
        }

        return valid
            ? new RsiMeanReversionStrategy(parameters.Period, parameters.Oversold, parameters.Overbought)
            : null;
    }

    private static IBacktestStrategy? Breakout(
        BreakoutParameters parameters,
        Dictionary<string, string[]> errors)
    {
        const string prefix = "breakout";
        var valid = true;

        if (parameters.EntryPeriod is < 1 or > MaxChannelPeriod)
        {
            errors[$"{prefix}.entryPeriod"] = [$"The entry channel must be between 1 and {MaxChannelPeriod} days."];
            valid = false;
        }

        if (parameters.ExitPeriod is < 1 or > MaxChannelPeriod)
        {
            errors[$"{prefix}.exitPeriod"] = [$"The exit channel must be between 1 and {MaxChannelPeriod} days."];
            valid = false;
        }

        return valid
            ? new DonchianBreakoutStrategy(parameters.EntryPeriod, parameters.ExitPeriod)
            : null;
    }

    private static IBacktestStrategy? Invalid(
        Dictionary<string, string[]> errors,
        string field,
        string message)
    {
        errors[field] = [message];
        return null;
    }

    private static void Range(
        Dictionary<string, string[]> errors,
        string field,
        decimal value,
        decimal min,
        decimal max,
        string message)
    {
        if (value < min || value > max)
        {
            errors[field] = [message];
        }
    }
}
