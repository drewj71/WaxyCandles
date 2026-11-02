using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;

namespace WaxyCandles.Infrastructure.Messaging;

public class RabbitMqConnection : IAsyncDisposable
{
    private readonly IConnection _connection;

    public RabbitMqConnection(IConfiguration configuration)
    {
        var host =
            configuration["RabbitMQ:Host"]
            ?? "localhost";

        var port =
            configuration.GetValue<int?>("RabbitMQ:Port")
            ?? AmqpTcpEndpoint.UseDefaultPort;

        var factory = new ConnectionFactory
        {
            HostName = host,
            Port = port
        };

        // Optional; each unset or blank one keeps the client default
        // (guest / guest on "/"), which RabbitMQ only accepts from localhost.
        // RabbitMQ:Password is a secret: user-secrets or RabbitMQ__Password.
        if (configuration["RabbitMQ:Username"] is { Length: > 0 } username)
        {
            factory.UserName = username;
        }

        if (configuration["RabbitMQ:Password"] is { Length: > 0 } password)
        {
            factory.Password = password;
        }

        if (configuration["RabbitMQ:VirtualHost"] is { Length: > 0 } virtualHost)
        {
            factory.VirtualHost = virtualHost;
        }

        _connection = factory
            .CreateConnectionAsync()
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// False while the connection is down. Channels on it are then restored by
    /// the client's automatic recovery, so callers shouldn't rebuild them.
    /// </summary>
    public bool IsOpen => _connection.IsOpen;

    public async Task<IChannel> CreateChannelAsync(
        CancellationToken cancellationToken = default)
    {
        return await _connection.CreateChannelAsync(
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// A channel with publisher confirms: BasicPublishAsync only completes once the
    /// broker has accepted the message, and throws if it is nacked or (when
    /// mandatory) unroutable. Use it wherever a publish is followed by marking
    /// something done (an outbox row, an ack of the original delivery).
    /// </summary>
    public async Task<IChannel> CreateConfirmedChannelAsync(
        CancellationToken cancellationToken = default)
    {
        return await _connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}