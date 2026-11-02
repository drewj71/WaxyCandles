namespace WaxyCandles.Application.Options.Gex;

/// <summary>
/// The chain slice actually fetched (and cached) for a <see cref="GexRequest"/>:
/// each range is rounded up to the next step of a short fixed ladder. Any
/// request is then answered from one of a few boards per symbol, so
/// near-identical ranges (10, 10.0001, 10.0002, ...) can't each cost a
/// provider call. The table is still built from the exact requested window,
/// so the result is unchanged; only a request between steps fetches more
/// than it shows.
/// </summary>
/// <remarks>
/// The ladders contain the web app's presets (1W/1M/3M/6M, ±5/10/20/30%) and
/// the defaults, so those fetch exactly what they show.
/// </remarks>
public static class GexFetchWindow
{
    public static readonly IReadOnlyList<int> DaySteps =
        [0, 7, 14, 30, 60, 90, 180, GexRequest.MaxDays];

    public static readonly IReadOnlyList<decimal> StrikeRangeSteps =
        [1m, 2m, 5m, 10m, 15m, 20m, 30m, 40m, GexRequest.MaxStrikeRangePercent];

    /// <summary>The smallest ladder window that covers <paramref name="request"/>, which must already be valid.</summary>
    public static GexRequest For(GexRequest request) =>
        new(
            DaySteps.First(x => x >= request.Days),
            StrikeRangeSteps.First(x => x >= request.StrikeRangePercent));
}
