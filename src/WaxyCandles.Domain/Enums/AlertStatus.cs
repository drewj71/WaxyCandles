namespace WaxyCandles.Domain.Enums;

/// <summary>What the alert is doing now, derived from its state (not stored).</summary>
public enum AlertStatus
{
    /// <summary>Armed: will fire when the condition is met.</summary>
    Active = 1,

    /// <summary>
    /// Fired. A one-time alert stays here; a repeating alert re-arms (Active)
    /// once the price moves back across its target.
    /// </summary>
    Triggered = 2,

    Disabled = 3,

    Expired = 4
}
