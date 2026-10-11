using System.Text;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using WaxyCandles.Infrastructure.Messaging;
using WaxyCandles.Infrastructure.Persistence;

namespace WaxyCandles.OutboxWorker;

public class OutboxPublisherWorker : BackgroundService
{
    private const int BatchSize = 20;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

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
        IChannel? channel = null;
        var queueDeclared = false;

        _logger.LogInformation(
            "Outbox publisher started.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var publishedFullBatch = false;

                try
                {
                    // Automatic recovery covers a lost connection, but not a
                    // channel the broker closed with a channel-level error.
                    // Without this the worker would fail every poll forever.
                    if (channel is null || channel.IsClosed)
                    {
                        if (channel is not null && !_rabbitMqConnection.IsOpen)
                        {
                            // The connection itself is down; automatic recovery
                            // will reopen this channel. Skip this poll.
                            throw new InvalidOperationException(
                                "RabbitMQ connection is down; waiting for automatic recovery.");
                        }

                        if (channel is not null)
                        {
                            _logger.LogWarning(
                                "RabbitMQ channel was closed: {Reason}. Reopening.",
                                channel.CloseReason?.ReplyText);

                            await channel.DisposeAsync();
                        }

                        // Publisher confirms: a row is only marked processed once
                        // RabbitMQ has accepted (and, being persistent on a durable
                        // queue, stored) the message.
                        channel =
                            await _rabbitMqConnection.CreateConfirmedChannelAsync(
                                stoppingToken);

                        queueDeclared = false;
                    }

                    if (!queueDeclared)
                    {
                        await DeclareQueueAsync(channel, stoppingToken);
                        queueDeclared = true;
                    }

                    var published =
                        await PublishBatchAsync(channel, stoppingToken);

                    publishedFullBatch = published == BatchSize;
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (PublishReturnException ex)
                {
                    // Unroutable, e.g. the queue was deleted. The channel is still
                    // open; redeclare the queue on the next poll.
                    _logger.LogError(
                        ex,
                        "Error while publishing outbox messages.");

                    queueDeclared = false;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Error while publishing outbox messages.");
                }

                // Keep draining without waiting while there is a backlog.
                if (!publishedFullBatch)
                {
                    await Task.Delay(
                        PollInterval,
                        stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (channel is not null)
            {
                await channel.DisposeAsync();
            }
        }
    }

    private static async Task DeclareQueueAsync(
        IChannel channel,
        CancellationToken stoppingToken)
    {
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
    }

    /// <summary>Publishes the oldest pending messages. Returns how many were published.</summary>
    private async Task<int> PublishBatchAsync(
        IChannel channel,
        CancellationToken stoppingToken)
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
                .Take(BatchSize)
                .ToListAsync(stoppingToken);

        var published = 0;

        try
        {
            foreach (var message in messages)
            {
                var body =
                    Encoding.UTF8.GetBytes(
                        message.Payload);

                var properties =
                    new BasicProperties
                    {
                        Persistent = true,
                        MessageId = message.Id.ToString(),
                        Type = message.Type,
                        ContentType = "application/json"
                    };

                // mandatory: an unroutable message (e.g. the queue was
                // deleted) fails the publish instead of being dropped.
                await channel.BasicPublishAsync(
                    exchange: string.Empty,
                    routingKey: "stock-price-updated",
                    mandatory: true,
                    basicProperties: properties,
                    body: body,
                    cancellationToken: stoppingToken);

                message.ProcessedAt =
                    DateTimeOffset.UtcNow;

                published++;

                _logger.LogInformation(
                    "Published outbox message {MessageId}",
                    message.Id);
            }
        }
        finally
        {
            // Save whatever was confirmed, even if a later publish in the
            // batch failed, so those messages aren't sent again next poll.
            await dbContext.SaveChangesAsync(
                CancellationToken.None);
        }

        return published;
    }
}
