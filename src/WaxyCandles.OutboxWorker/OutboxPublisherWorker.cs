using System.Text;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using WaxyCandles.Infrastructure.Messaging;
using WaxyCandles.Infrastructure.Persistence;

namespace WaxyCandles.OutboxWorker;

public class OutboxPublisherWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqConnection _rabbitMqConnection;
    private readonly ILogger<OutboxPublisherWorker> _logger;

    public OutboxPublisherWorker(
        IServiceScopeFactory scopeFactory,
        RabbitMqConnection rabbitMqConnection,
        ILogger<OutboxPublisherWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _rabbitMqConnection = rabbitMqConnection;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        await using var channel =
            await _rabbitMqConnection.CreateChannelAsync(
                stoppingToken);

        await channel.QueueDeclareAsync(
            queue: "stock-price-updated",
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = "stock-price-retry",
                ["x-dead-letter-routing-key"] = "retry"
            },
            cancellationToken: stoppingToken);

        _logger.LogInformation(
            "Outbox publisher started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope =
                    _scopeFactory.CreateScope();

                var dbContext =
                    scope.ServiceProvider
                        .GetRequiredService<AppDbContext>();

                var messages =
                    await dbContext.OutboxMessages
                        .Where(x => x.ProcessedAt == null)
                        .OrderBy(x => x.CreatedAt)
                        .Take(20)
                        .ToListAsync(stoppingToken);

                foreach (var message in messages)
                {
                    var body =
                        Encoding.UTF8.GetBytes(
                            message.Payload);

                    await channel.BasicPublishAsync(
                        exchange: string.Empty,
                        routingKey: "stock-price-updated",
                        body: body,
                        cancellationToken: stoppingToken);

                    message.ProcessedAt =
                        DateTimeOffset.UtcNow;

                    _logger.LogInformation(
                        "Published outbox message {MessageId}",
                        message.Id);
                }

                await dbContext.SaveChangesAsync(
                    stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error while publishing outbox messages.");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(5),
                stoppingToken);
        }
    }
}