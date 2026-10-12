using System.Text.RegularExpressions;
using WaxyCandles.Application.Alerts.DTOs;
using WaxyCandles.Application.Common.Exceptions;
using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Application.Alerts.Validation;

public static partial class AlertRequestValidator
{
    public const int MinCooldownMinutes = 1;

    public const int MaxCooldownMinutes = 24 * 60;

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
            now);

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
            now);

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
        DateTimeOffset now)
    {
        if (targetPrice <= 0 || targetPrice > MaxTargetPrice)
        {
            errors["targetPrice"] = ["Target price must be greater than 0."];
        }

        if (!Enum.IsDefined(direction))
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

    private static void ThrowIfAny(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }
    }
}
