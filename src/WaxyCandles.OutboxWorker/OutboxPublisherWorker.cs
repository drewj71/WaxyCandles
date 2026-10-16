using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using WaxyCandles.Infrastructure.Messaging;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.Infrastructure.Telemetry;

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
                    // open; redeclare the queues on the next poll.
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

    // Declares the full topology of every route (same arguments as the
    // consumers), so messages are deliverable even before a consumer has run.
    private static async Task DeclareQueueAsync(
        IChannel channel,
        CancellationToken stoppingToken)
    {
        foreach (var topology in MessageRoutes.ByMessageType.Values.Distinct())
        {
            await topology.DeclareAsync(channel, stoppingToken);
        }
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

        var routedTypes = MessageRoutes.ByMessageType.Keys.ToList();

        // Rows of a type with no route are left pending (never marked
        // processed) and excluded here, so they can't block the rows behind them.
        var unroutable =
            await dbContext.OutboxMessages
                .CountAsync(
                    x => x.ProcessedAt == null &&
                         !routedTypes.Contains(x.Type),
                    stoppingToken);

        if (unroutable > 0)
        {
            _logger.LogError(
                "{Count} pending outbox message(s) have a type with no route in MessageRoutes; they will not be published.",
                unroutable);
        }

        var messages =
            await dbContext.OutboxMessages
                .Where(x =>
                    x.ProcessedAt == null &&
                    routedTypes.Contains(x.Type))
                .OrderBy(x => x.CreatedAt)
                .Take(BatchSize)
                .ToListAsync(stoppingToken);

        var published = 0;

        try
        {
            foreach (var message in messages)
            {
                // TrimEnd: SQL Server ignores trailing spaces when comparing, so
                // the query can select "Type " for "Type".
                var topology = MessageRoutes.ByMessageType[message.Type.TrimEnd()];

                // Continues the trace of whatever wrote the row; the gap
                // between that span and this one is the time spent waiting
                // in the outbox.
                using var activity = WaxyCandlesTelemetry.Source.StartActivity(
                    $"publish {topology.Queue}",
                    ActivityKind.Producer,
                    MessageTracing.FromOutbox(message));

                activity?.SetTag(WaxyCandlesTelemetry.MessagingSystem, "rabbitmq");
                activity?.SetTag(WaxyCandlesTelemetry.MessagingDestination, topology.Queue);
                activity?.SetTag(WaxyCandlesTelemetry.MessagingMessageId, message.Id.ToString());
                activity?.SetTag(
                    WaxyCandlesTelemetry.OutboxWaitMs,
                    (long)(DateTimeOffset.UtcNow - message.CreatedAt).TotalMilliseconds);

                var body =
                    Encoding.UTF8.GetBytes(
                        message.Payload);

                var headers = new Dictionary<string, object?>();
                MessageTracing.InjectCurrent(headers);

                var properties =
                    new BasicProperties
                    {
                        Persistent = true,
                        MessageId = message.Id.ToString(),
                        Type = message.Type,
                        ContentType = "application/json",
                        Headers = headers
                    };

                // mandatory: an unroutable message (e.g. the queue was
                // deleted) fails the publish instead of being dropped.
                await channel.BasicPublishAsync(
                    exchange: string.Empty,
                    routingKey: topology.Queue,
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
