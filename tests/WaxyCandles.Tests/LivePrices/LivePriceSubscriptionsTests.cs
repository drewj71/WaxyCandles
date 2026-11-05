using WaxyCandles.Infrastructure.LivePrices;

namespace WaxyCandles.Tests.LivePrices;

public class LivePriceSubscriptionsTests
{
    private readonly LivePriceSubscriptions _subscriptions = new();

    [Fact]
    public void A_symbol_stays_watched_until_its_last_connection_leaves()
    {
        Assert.Equal(SubscribeResult.Added, _subscriptions.Subscribe("c1", "AAPL", 10));
        Assert.Equal(SubscribeResult.Added, _subscriptions.Subscribe("c2", "AAPL", 10));
        Assert.Equal(SubscribeResult.AlreadySubscribed, _subscriptions.Subscribe("c1", "AAPL", 10));

        _subscriptions.RemoveConnection("c1");
        Assert.Equal(["AAPL"], _subscriptions.Symbols());

        Assert.True(_subscriptions.Unsubscribe("c2", "AAPL"));
        Assert.Empty(_subscriptions.Symbols());
        Assert.Equal(0, _subscriptions.ConnectionCount);
    }

    [Fact]
    public void Disconnect_removes_every_symbol_of_the_connection()
    {
        _subscriptions.Subscribe("c1", "AAPL", 10);
        _subscriptions.Subscribe("c1", "MSFT", 10);
        _subscriptions.Subscribe("c2", "MSFT", 10);

        _subscriptions.RemoveConnection("c1");

        Assert.Equal(["MSFT"], _subscriptions.Symbols());
        Assert.Empty(_subscriptions.SymbolsFor("c1"));

        // Removing again, or an unknown connection, is harmless.
        _subscriptions.RemoveConnection("c1");
        _subscriptions.RemoveConnection("unknown");
        Assert.False(_subscriptions.Unsubscribe("c1", "AAPL"));
    }

    [Fact]
    public void The_per_connection_limit_counts_only_that_connection()
    {
        Assert.Equal(SubscribeResult.Added, _subscriptions.Subscribe("c1", "A", 2));
        Assert.Equal(SubscribeResult.Added, _subscriptions.Subscribe("c1", "B", 2));
        Assert.Equal(SubscribeResult.LimitReached, _subscriptions.Subscribe("c1", "C", 2));

        // Already watched is not a new subscription, even at the limit.
        Assert.Equal(SubscribeResult.AlreadySubscribed, _subscriptions.Subscribe("c1", "A", 2));
        Assert.Equal(SubscribeResult.Added, _subscriptions.Subscribe("c2", "C", 2));

        _subscriptions.Unsubscribe("c1", "A");
        Assert.Equal(SubscribeResult.Added, _subscriptions.Subscribe("c1", "C", 2));

        Assert.Equal(["B", "C"], _subscriptions.SymbolsFor("c1").Order());
    }

    [Fact]
    public async Task Concurrent_subscribes_and_disconnects_leave_a_consistent_count()
    {
        var work = Enumerable.Range(0, 50).Select(i => Task.Run(() =>
        {
            var connection = $"c{i}";
            _subscriptions.Subscribe(connection, "AAPL", 10);
            _subscriptions.Subscribe(connection, $"S{i % 5}", 10);
            _subscriptions.RemoveConnection(connection);
        }));

        await Task.WhenAll(work);

        Assert.Empty(_subscriptions.Symbols());
        Assert.Equal(0, _subscriptions.ConnectionCount);
    }
}
