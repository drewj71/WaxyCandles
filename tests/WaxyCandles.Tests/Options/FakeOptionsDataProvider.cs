using System.Collections.Concurrent;
using WaxyCandles.Application.Options.Interfaces;
using WaxyCandles.Application.Options.Models;

namespace WaxyCandles.Tests.Options;

/// <summary>Returns <see cref="Chain"/> for every query and records the queries.</summary>
public sealed class FakeOptionsDataProvider : IOptionsDataProvider
{
    private readonly ConcurrentQueue<OptionChainQuery> _queries = new();

    public OptionChain? Chain { get; set; }

    public IReadOnlyList<OptionChainQuery> Queries => _queries.ToArray();

    public Task<OptionChain?> GetOptionChainAsync(
        OptionChainQuery query,
        CancellationToken cancellationToken = default)
    {
        _queries.Enqueue(query);

        return Task.FromResult(Chain);
    }
}
