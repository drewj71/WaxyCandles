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

    /// <summary>How often queue depths (the waxycandles.queue.messages gauge) are refreshed.</summary>
    public static readonly TimeSpan QueueDepthInterval = TimeSpan.FromSeconds(15);

    // A refresh runs on the publish loop, so a slow broker must not stall
    // publishing for long (each passive declare could otherwise wait out the
    // client's continuation timeout).
    private static readonly TimeSpan QueueDepthTimeout = TimeSpan.FromSeconds(5);

    private readonly IServiceProvider _services;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxPublisherWorker> _logger;

    // RabbitMqConnection is deliberately not injected: it connects in its
    // constructor, so injecting it would make constructing this hosted service
    // (and so starting the host) fail while the broker is unreachable.
    public OutboxPublisherWorker(
        IServiceProvider services,
        IServiceScopeFactory scopeFactory,
        ILogger<OutboxPublisherWorker> logger)
    {
        _services = services;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        // Let host startup finish before anything touches the broker.
        await Task.Yield();

        IChannel? channel = null;
        var queueDeclared = false;

        // Only for passive declares (queue depth). A passive declare of a
        // missing queue makes the broker close the channel (404), so it must
        // never be the publish channel.
        IChannel? depthChannel = null;
        var nextQueueDepthAt = DateTimeOffset.MinValue;

        _logger.LogInformation(
            "Outbox publisher started.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var publishedFullBatch = false;

                // Before (and independent of) the broker, so the backlog
                // metrics keep updating while RabbitMQ is down, which is when
                // the backlog grows.
                try
                {
                    await RefreshBacklogAsync(stoppingToken);
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
                        "Error while reading the outbox backlog.");
                }

                try
                {
                    // Resolved per poll: the connection is opened on first
                    // resolve, and DI doesn't cache a singleton whose
                    // constructor threw, so a broker that is down at startup
                    // is retried here after PollInterval like any publish error.
                    var connection =
                        _services.GetRequiredService<RabbitMqConnection>();

                    // Automatic recovery covers a lost connection, but not a
                    // channel the broker closed with a channel-level error.
                    // Without this the worker would fail every poll forever.
                    if (channel is null || channel.IsClosed)
                    {
                        if (channel is not null && !connection.IsOpen)
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
                            await connection.CreateConfirmedChannelAsync(
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

                // After publishing and on its own channel: a failure here only
                // makes the queue depth gauge go quiet. Skipped while the publish
                // channel is down, so an unreachable broker isn't sent extra
                // connection attempts (the connection's constructor blocks and
                // ignores cancellation).
                if (DateTimeOffset.UtcNow >= nextQueueDepthAt &&
                    channel is not { IsOpen: true })
                {
                    nextQueueDepthAt = DateTimeOffset.UtcNow + QueueDepthInterval;

                    // Unknown rather than stale.
                    WaxyCandlesMetrics.ClearQueueDepths();
                }
                else if (DateTimeOffset.UtcNow >= nextQueueDepthAt)
                {
                    nextQueueDepthAt = DateTimeOffset.UtcNow + QueueDepthInterval;

                    try
                    {
                        using var refreshTimeout =
                            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        refreshTimeout.CancelAfter(QueueDepthTimeout);

                        depthChannel = await RefreshQueueDepthsAsync(
                            depthChannel,
                            refreshTimeout.Token);
                    }
                    catch (OperationCanceledException)
                        when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Unknown rather than stale.
                        WaxyCandlesMetrics.ClearQueueDepths();

                        // Start the next refresh on a fresh channel.
                        if (depthChannel is not null)
                        {
                            try
                            {
                                await depthChannel.DisposeAsync();
                            }
                            catch (Exception)
                            {
                                // Already broken; nothing to clean up.
                            }

                            depthChannel = null;
                        }

                        _logger.LogWarning(
                            ex,
                            "Could not read RabbitMQ queue depths.");
                    }
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

            if (depthChannel is not null)
            {
                await depthChannel.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Stores the backlog (routed pending count, oldest pending age, unroutable
    /// count) for the outbox gauges, which never query the database themselves.
    /// </summary>
    private async Task RefreshBacklogAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var dbContext =
            scope.ServiceProvider
                .GetRequiredService<AppDbContext>();

        var routedTypes = MessageRoutes.ByMessageType.Keys.ToList();

        // Rows of a type with no route are left pending (never marked
        // processed) and never published, so they're reported separately.
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

        // One aggregate query: no rows means no backlog.
        var backlog =
            await dbContext.OutboxMessages
                .Where(x =>
                    x.ProcessedAt == null &&
                    routedTypes.Contains(x.Type))
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Pending = g.Count(),
                    Oldest = g.Min(x => x.CreatedAt)
                })
                .SingleOrDefaultAsync(stoppingToken);

        var oldestAgeSeconds = backlog is null
            ? 0
            : Math.Max(0, (DateTimeOffset.UtcNow - backlog.Oldest).TotalSeconds);

        WaxyCandlesMetrics.SetOutboxBacklog(
            backlog?.Pending ?? 0,
            oldestAgeSeconds,
            unroutable);
    }

    /// <summary>
    /// Passively declares each routed queue, its retry queue and its
    /// dead-letter queue, and stores their ready-message counts. A missing
    /// queue (404) closes <paramref name="depthChannel"/>; it is reopened for
    /// the next queue. Returns the channel to reuse next time.
    /// </summary>
    private async Task<IChannel?> RefreshQueueDepthsAsync(
        IChannel? depthChannel,
        CancellationToken stoppingToken)
    {
        var connection = _services.GetRequiredService<RabbitMqConnection>();

        foreach (var topology in MessageRoutes.ByMessageType.Values.Distinct())
        {
            foreach (var queue in (string[])[topology.Queue, topology.RetryQueue, topology.DeadLetterQueue])
            {
                if (depthChannel is null || depthChannel.IsClosed)
                {
                    if (depthChannel is not null)
                    {
                        await depthChannel.DisposeAsync();
                    }

                    // No confirms: nothing is published on it.
                    depthChannel = await connection.CreateChannelAsync(stoppingToken);
                }

                try
                {
                    var ok = await depthChannel.QueueDeclarePassiveAsync(
                        queue,
                        stoppingToken);

                    WaxyCandlesMetrics.SetQueueDepth(queue, ok.MessageCount);
                }
                catch (OperationInterruptedException ex)
                    when (ex.ShutdownReason?.ReplyCode == 404)
                {
                    // Not declared (yet). The broker closed the channel.
                    WaxyCandlesMetrics.ClearQueueDepth(queue);
                }
            }
        }

        return depthChannel;
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
        // processed) and excluded here, so they can't block the rows behind
        // them. RefreshBacklogAsync counts and logs them.
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

                WaxyCandlesMetrics.OutboxPublished.Add(
                    1,
                    WaxyCandlesMetrics.Queue(topology.Queue));

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
