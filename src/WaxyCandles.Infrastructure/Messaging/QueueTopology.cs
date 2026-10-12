using RabbitMQ.Client;

namespace WaxyCandles.Infrastructure.Messaging;

/// <summary>
/// A work queue with its retry and dead-letter plumbing:
/// <c>Queue</c> -> (failure) -> <c>RetryExchange</c>/<c>RetryQueue</c>, which holds
/// a message for <see cref="RetryDelay"/> then dead-letters it back to <c>Queue</c>;
/// after the last retry -> <c>DeadLetterExchange</c>/<c>DeadLetterQueue</c>.
/// Producers and consumers both declare it, with identical arguments (RabbitMQ
/// rejects a redeclaration whose arguments differ), so it lives in one place.
/// </summary>
public sealed record QueueTopology(
    string Queue,
    string RetryExchange,
    string RetryQueue,
    string DeadLetterExchange,
    string DeadLetterQueue)
{
    public const string RetryRoutingKey = "retry";

    public const string DeadLetterRoutingKey = "dead-letter";

    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    public static readonly QueueTopology StockPriceUpdated = new(
        Queue: "stock-price-updated",
        RetryExchange: "stock-price-retry",
        RetryQueue: "stock-price-retry",
        DeadLetterExchange: "stock-price-dlx",
        DeadLetterQueue: "stock-price-dead-letter");

    public static readonly QueueTopology AlertTriggered = new(
        Queue: "alert-triggered",
        RetryExchange: "alert-triggered-retry",
        RetryQueue: "alert-triggered-retry",
        DeadLetterExchange: "alert-triggered-dlx",
        DeadLetterQueue: "alert-triggered-dead-letter");

    public async Task DeclareAsync(
        IChannel channel,
        CancellationToken cancellationToken = default)
    {
        await channel.ExchangeDeclareAsync(
            exchange: RetryExchange,
            type: "direct",
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        await channel.ExchangeDeclareAsync(
            exchange: DeadLetterExchange,
            type: "direct",
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: RetryQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = (int)RetryDelay.TotalMilliseconds,
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = Queue
            },
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: RetryQueue,
            exchange: RetryExchange,
            routingKey: RetryRoutingKey,
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: DeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: DeadLetterQueue,
            exchange: DeadLetterExchange,
            routingKey: DeadLetterRoutingKey,
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: Queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = RetryExchange,
                ["x-dead-letter-routing-key"] = RetryRoutingKey
            },
            cancellationToken: cancellationToken);
    }
}
