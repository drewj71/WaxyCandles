namespace WaxyCandles.Domain.Enums;

public enum AlertFrequency
{
    /// <summary>Fires the first time the condition is met, then stays triggered.</summary>
    Once = 1,

    /// <summary>
    /// Fires each time the price crosses the target: after firing it re-arms once
    /// the price moves back across, and fires again no sooner than its cooldown.
    /// </summary>
    EveryCross = 2
}
