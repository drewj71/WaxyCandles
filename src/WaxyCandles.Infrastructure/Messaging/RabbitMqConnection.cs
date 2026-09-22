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

        var factory = new ConnectionFactory
        {
            HostName = host
        };

        _connection = factory
            .CreateConnectionAsync()
            .GetAwaiter()
            .GetResult();
    }

    public async Task<IChannel> CreateChannelAsync(
        CancellationToken cancellationToken = default)
    {
        return await _connection.CreateChannelAsync(
            cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}