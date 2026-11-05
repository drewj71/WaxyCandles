namespace WaxyCandles.Infrastructure.LivePrices;

public enum SubscribeResult
{
    Added,
    AlreadySubscribed,
    LimitReached
}

/// <summary>
/// Which symbols each connection on this instance watches (in memory; every
/// API instance serves only its own connections). Thread-safe: hub calls and
/// disconnects arrive on many threads while the broadcaster reads
/// <see cref="Symbols"/>. Symbols are expected normalized.
/// </summary>
public sealed class LivePriceSubscriptions
{
    private readonly object _gate = new();

    private readonly Dictionary<string, HashSet<string>> _byConnection = new(StringComparer.Ordinal);

    // How many connections watch each symbol; a symbol leaves when it hits 0.
    private readonly Dictionary<string, int> _watchers = new(StringComparer.Ordinal);

    public SubscribeResult Subscribe(string connectionId, string symbol, int maxPerConnection)
    {
        lock (_gate)
        {
            if (!_byConnection.TryGetValue(connectionId, out var symbols))
            {
                symbols = new HashSet<string>(StringComparer.Ordinal);
                _byConnection[connectionId] = symbols;
            }

            if (symbols.Contains(symbol))
            {
                return SubscribeResult.AlreadySubscribed;
            }

            if (symbols.Count >= maxPerConnection)
            {
                if (symbols.Count == 0)
                {
                    _byConnection.Remove(connectionId);
                }

                return SubscribeResult.LimitReached;
            }

            symbols.Add(symbol);
            _watchers[symbol] = _watchers.GetValueOrDefault(symbol) + 1;

            return SubscribeResult.Added;
        }
    }

    /// <summary>False if the connection wasn't subscribed to it.</summary>
    public bool Unsubscribe(string connectionId, string symbol)
    {
        lock (_gate)
        {
            if (!_byConnection.TryGetValue(connectionId, out var symbols) ||
                !symbols.Remove(symbol))
            {
                return false;
            }

            if (symbols.Count == 0)
            {
                _byConnection.Remove(connectionId);
            }

            Release(symbol);
            return true;
        }
    }

    /// <summary>Drops everything a closed connection watched.</summary>
    public void RemoveConnection(string connectionId)
    {
        lock (_gate)
        {
            if (!_byConnection.Remove(connectionId, out var symbols))
            {
                return;
            }

            foreach (var symbol in symbols)
            {
                Release(symbol);
            }
        }
    }

    /// <summary>A snapshot of the symbols at least one connection watches.</summary>
    public IReadOnlyList<string> Symbols()
    {
        lock (_gate)
        {
            return _watchers.Keys.ToList();
        }
    }

    public int ConnectionCount
    {
        get
        {
            lock (_gate)
            {
                return _byConnection.Count;
            }
        }
    }

    public IReadOnlyList<string> SymbolsFor(string connectionId)
    {
        lock (_gate)
        {
            return _byConnection.TryGetValue(connectionId, out var symbols)
                ? symbols.ToList()
                : [];
        }
    }

    private void Release(string symbol)
    {
        var remaining = _watchers[symbol] - 1;

        if (remaining == 0)
        {
            _watchers.Remove(symbol);
        }
        else
        {
            _watchers[symbol] = remaining;
        }
    }
}
