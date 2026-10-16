using System.Collections.Concurrent;
using WaxyCandles.Application.Alerts.Interfaces;
using WaxyCandles.Application.Alerts.Levels;

namespace WaxyCandles.Tests.Alerts;

/// <summary>Boards by symbol; a symbol in <see cref="Failing"/> throws.</summary>
public sealed class FakeGexLevelBoardSource : IGexLevelBoardSource
{
    private readonly ConcurrentQueue<(string Symbol, int Days, decimal Range)> _requests = new();

    public Dictionary<string, GexLevelBoard?> Boards { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Failing { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<(string Symbol, int Days, decimal Range)> Requests => _requests.ToArray();

    /// <summary>Runs on each request before answering, e.g. to race a competing write.</summary>
    public Func<Task>? OnRequest { get; set; }

    public async Task<GexLevelBoard?> GetAsync(
        string symbol,
        int days,
        decimal strikeRangePercent,
        CancellationToken cancellationToken = default)
    {
        _requests.Enqueue((symbol, days, strikeRangePercent));

        if (OnRequest is not null)
        {
            await OnRequest();
        }

        if (Failing.Contains(symbol))
        {
            throw new HttpRequestException($"Simulated provider failure for {symbol}");
        }

        return Boards.GetValueOrDefault(symbol);
    }
}
