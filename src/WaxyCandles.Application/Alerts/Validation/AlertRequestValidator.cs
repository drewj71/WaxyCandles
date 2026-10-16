using System.Text.RegularExpressions;
using WaxyCandles.Application.Alerts.DTOs;
using WaxyCandles.Application.Common.Exceptions;
using WaxyCandles.Application.Options.Gex;
using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Application.Alerts.Validation;

public static partial class AlertRequestValidator
{
    public const int MinCooldownMinutes = 1;

    public const int MaxCooldownMinutes = 24 * 60;

    public const decimal MaxLevelProximityPercent = 5m;

    // Prices are stored as decimal(18,6): 12 digits before the point.
    private const decimal MaxTargetPrice = 999_999_999_999m;

    [GeneratedRegex("^[A-Z0-9.-]{1,10}$")]
    private static partial Regex SymbolPattern();

    /// <summary>Uppercased and trimmed, as alerts store it.</summary>
    public static string NormalizeSymbol(string? symbol) =>
        (symbol ?? string.Empty).Trim().ToUpperInvariant();

    /// <exception cref="RequestValidationException">The request is invalid.</exception>
    public static void Validate(CreateAlertRequest request, DateTimeOffset now)
    {
        var errors = new Dictionary<string, string[]>();

        if (!SymbolPattern().IsMatch(NormalizeSymbol(request.Symbol)))
        {
            errors["symbol"] =
            [
                "Symbol must be 1-10 characters: letters, digits, '.' or '-'."
            ];
        }

        ValidateSettings(
            errors,
            request.TargetPrice,
            request.Direction,
            request.Frequency,
            request.CooldownMinutes,
            request.ExpiresAt,
            checkExpiry: true,
            now,
            isLevelAlert: request.Level is not null);

        ValidateLevel(
            errors,
            request.Level,
            request.LevelProximityPercent,
            request.LevelDays,
            request.LevelStrikeRange);

        ThrowIfAny(errors);
    }

    /// <param name="currentExpiresAt">
    /// The alert's expiry before this update. Sending it back unchanged is allowed
    /// even once it has passed, so an expired alert can still be edited or toggled.
    /// </param>
    /// <exception cref="RequestValidationException">The request is invalid.</exception>
    public static void Validate(
        UpdateAlertRequest request,
        DateTimeOffset? currentExpiresAt,
        DateTimeOffset now)
    {
        var errors = new Dictionary<string, string[]>();

        ValidateSettings(
            errors,
            request.TargetPrice,
            request.Direction,
            request.Frequency,
            request.CooldownMinutes,
            request.ExpiresAt,
            checkExpiry: request.ExpiresAt != currentExpiresAt,
            now,
            isLevelAlert: request.Level is not null);

        ValidateLevel(
            errors,
            request.Level,
            request.LevelProximityPercent,
            request.LevelDays,
            request.LevelStrikeRange);

        ThrowIfAny(errors);
    }

    private static void ValidateSettings(
        Dictionary<string, string[]> errors,
        decimal targetPrice,
        AlertDirection direction,
        AlertFrequency frequency,
        int cooldownMinutes,
        DateTimeOffset? expiresAt,
        bool checkExpiry,
        DateTimeOffset now,
        bool isLevelAlert)
    {
        // A level alert's target and direction are derived, not sent.
        if (!isLevelAlert && (targetPrice <= 0 || targetPrice > MaxTargetPrice))
        {
            errors["targetPrice"] = ["Target price must be greater than 0."];
        }

        if (!isLevelAlert && !Enum.IsDefined(direction))
        {
            errors["direction"] = ["Direction must be Above or Below."];
        }

        if (!Enum.IsDefined(frequency))
        {
            errors["frequency"] = ["Frequency must be Once or EveryCross."];
        }

        if (cooldownMinutes is < MinCooldownMinutes or > MaxCooldownMinutes)
        {
            errors["cooldownMinutes"] =
            [
                $"Cooldown must be between {MinCooldownMinutes} and {MaxCooldownMinutes} minutes."
            ];
        }

        if (checkExpiry && expiresAt <= now)
        {
            errors["expiresAt"] = ["Expiry must be in the future."];
        }
    }

    private static void ValidateLevel(
        Dictionary<string, string[]> errors,
        GexLevelKind? level,
        decimal proximityPercent,
        int days,
        decimal strikeRange)
    {
        if (level is null)
        {
            return;
        }

        if (!Enum.IsDefined(level.Value))
        {
            errors["level"] = ["Level must be King, Gatekeeper, Floor or Ceiling."];
        }

        if (proximityPercent is < 0 or > MaxLevelProximityPercent)
        {
            errors["levelProximityPercent"] =
            [
                $"Proximity must be between 0 and {MaxLevelProximityPercent}%."
            ];
        }

        if (days is < 0 or > GexRequest.MaxDays)
        {
            errors["levelDays"] = [$"Days must be between 0 and {GexRequest.MaxDays}."];
        }

        if (strikeRange is <= 0 or > GexRequest.MaxStrikeRangePercent)
        {
            errors["levelStrikeRange"] =
            [
                $"Strike range must be more than 0 and at most {GexRequest.MaxStrikeRangePercent}%."
            ];
        }
    }

    private static void ThrowIfAny(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }
    }
}
