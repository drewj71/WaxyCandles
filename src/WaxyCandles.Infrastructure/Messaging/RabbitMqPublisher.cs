using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

namespace WaxyCandles.Infrastructure.Messaging;

public class RabbitMqPublisher : IRabbitMqPublisher
{
    private readonly RabbitMqConnection _rabbitMqConnection;

    public RabbitMqPublisher(
        RabbitMqConnection rabbitMqConnection)
    {
        _rabbitMqConnection = rabbitMqConnection;
    }

    public async Task PublishAsync<T>(
        T message,
        string queueName,
        CancellationToken cancellationToken = default)
    {
        await using var channel =
            await _rabbitMqConnection.CreateChannelAsync(
                cancellationToken);

        await channel.QueueDeclareAsync(
            queue: queueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        var json = JsonSerializer.Serialize(message);

        var body = Encoding.UTF8.GetBytes(json);

        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: queueName,
            body: body,
            cancellationToken: cancellationToken);
    }
}