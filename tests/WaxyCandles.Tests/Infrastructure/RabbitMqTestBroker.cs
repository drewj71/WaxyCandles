using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using WaxyCandles.Application.MarketData.Messages;

namespace WaxyCandles.Tests.Infrastructure;

/// <summary>
/// A test-side connection to the RabbitMQ container for publishing, inspecting
/// and resetting the app's (hardcoded) queue topology.
/// </summary>
public sealed class RabbitMqTestBroker : IAsyncDisposable
{
    public const string MainQueue = "stock-price-updated";
    public const string RetryQueue = "stock-price-retry";
    public const string DeadLetterQueue = "stock-price-dead-letter";
    public const string RetryExchange = "stock-price-retry";
    public const string DeadLetterExchange = "stock-price-dlx";

    private readonly IConnection _connection;

    private RabbitMqTestBroker(IConnection connection)
    {
        _connection = connection;
    }

    public static async Task<RabbitMqTestBroker> ConnectAsync(IntegrationFixture fixture)
    {
        var connection = await new ConnectionFactory
        {
            HostName = fixture.RabbitMqHost,
            Port = fixture.RabbitMqPort
        }.CreateConnectionAsync();

        return new RabbitMqTestBroker(connection);
    }

    public static byte[] Serialize(StockPriceUpdatedMessage message) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

    public Task PublishAsync(StockPriceUpdatedMessage message) =>
        PublishAsync(Serialize(message));

    public async Task PublishAsync(byte[] body)
    {
        await using var channel = await _connection.CreateChannelAsync();

        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: MainQueue,
            mandatory: false,
            basicProperties: new BasicProperties { Persistent = true },
            body: body);
    }

    public async Task<BasicGetResult?> GetAsync(string queue)
    {
        await using var channel = await _connection.CreateChannelAsync();
        return await channel.BasicGetAsync(queue, autoAck: true);
    }

    public async Task<uint> MessageCountAsync(string queue) =>
        (await DeclarePassiveAsync(queue))?.MessageCount ?? 0;

    public async Task<uint> ConsumerCountAsync(string queue) =>
        (await DeclarePassiveAsync(queue))?.ConsumerCount ?? 0;

    /// <summary>
    /// Deletes every queue and exchange the workers declare, so each test starts clean.
    /// </summary>
    public async Task DeleteTopologyAsync()
    {
        foreach (var queue in new[] { MainQueue, RetryQueue, DeadLetterQueue })
        {
            await using var channel = await _connection.CreateChannelAsync();
            await channel.QueueDeleteAsync(queue);
        }

        foreach (var exchange in new[] { RetryExchange, DeadLetterExchange })
        {
            await using var channel = await _connection.CreateChannelAsync();
            await channel.ExchangeDeleteAsync(exchange);
        }
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private async Task<QueueDeclareOk?> DeclarePassiveAsync(string queue)
    {
        // A passive declare of a missing queue closes the channel, so use a throwaway one.
        await using var channel = await _connection.CreateChannelAsync();

        try
        {
            return await channel.QueueDeclarePassiveAsync(queue);
        }
        catch (OperationInterruptedException)
        {
            return null;
        }
    }
}
