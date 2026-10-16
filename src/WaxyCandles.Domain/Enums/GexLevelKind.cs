namespace WaxyCandles.Domain.Enums;

/// <summary>A named node on a symbol's GEX board, as an alert target.</summary>
public enum GexLevelKind
{
    /// <summary>The largest absolute node on the board.</summary>
    King = 1,

    /// <summary>The largest node between the price and the King.</summary>
    Gatekeeper = 2,

    /// <summary>The largest node below the price.</summary>
    Floor = 3,

    /// <summary>The largest node above the price.</summary>
    Ceiling = 4
}
