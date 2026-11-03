using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using WaxyCandles.Infrastructure.Telemetry;

namespace WaxyCandles.Infrastructure.Messaging;

public enum ConsumeOutcome
{
    Processed,

    /// <summary>A redelivery of a message that was already processed.</summary>
    AlreadyProcessed,

    /// <summary>Nothing to do, and retrying won't change that. The subclass logs why.</summary>
    Skipped
}

/// <summary>
/// The transport side of a consumer on a <see cref="QueueTopology"/>:
/// (re)connecting, deserializing, retry -> dead-letter on failure, and never
/// losing or stranding a delivery. Subclasses only validate and process
/// messages, and are responsible for making processing idempotent.
/// </summary>
public abstract class RabbitMqConsumer<TMessage> : BackgroundService
    where TMessage : class
{
    // How long to wait before reopening a lost channel, and before requeueing a
    // delivery whose retry/dead-letter publish failed (avoids a hot redelivery loop).
    private static readonly TimeSpan RecoveryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Unacked deliveries the broker may push to one consumer
    /// (<c>RabbitMQ:PrefetchCount</c> overrides it). Without a limit the broker
    /// pushes a whole backlog into one consumer's memory, and a second instance
    /// gets none of it. Deliveries are still handled one at a time.
    /// </summary>
    public const ushort DefaultPrefetchCount = 10;

    public const string PrefetchCountKey = "RabbitMQ:PrefetchCount";

    private readonly QueueTopology _topology;
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;

    protected RabbitMqConsumer(
        QueueTopology topology,
        IServiceProvider services,
        ILogger logger)
    {
        _topology = topology;
        _services = services;
        _logger = logger;
    }

    /// <summary>Used in log messages, e.g. "stock price".</summary>
    protected abstract string MessageDescription { get; }

    protected abstract Guid GetMessageId(TMessage message);

    /// <summary>
    /// False for a message that can never be processed (missing or nonsensical
    /// fields). It is dead-lettered immediately instead of being retried.
    /// </summary>
    protected abstract bool IsValid(TMessage message);

    /// <summary>
    /// Processes one message within its own DI scope. Throw to have it retried
    /// (and dead-lettered after the last retry).
    /// </summary>
    protected abstract Task<ConsumeOutcome> ProcessAsync(
        TMessage message,
        IServiceProvider scopedServices,
        CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        // BackgroundService.StartAsync runs this synchronously up to the first
        // await, and opening the connection below blocks (it can take a full
        // connect timeout when the broker is unreachable). Yield first so the
        // host's startup never waits on RabbitMQ.
        await Task.Yield();

        // Connection loss is handled by RabbitMQ.Client's automatic recovery, but a
        // channel the broker closes (channel-level error) or a subscription it
        // cancels (queue deleted) is not. Rebuild both here so the worker can't
        // stall silently while its process still looks healthy.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeUntilChannelLostAsync(stoppingToken);
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
                    "Consumer for {Queue} failed.",
                    _topology.Queue);
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            _logger.LogWarning(
                "RabbitMQ channel or subscription lost. Reconnecting in {Delay}.",
                RecoveryDelay);

            try
            {
                await Task.Delay(RecoveryDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ConsumeUntilChannelLostAsync(
        CancellationToken stoppingToken)
    {
        // Resolved here rather than injected: the connection is opened when it is
        // first resolved, and this way a broker that is down at startup is retried
        // by the loop above instead of failing the whole host.
        var connection = _services.GetRequiredService<RabbitMqConnection>();

        // Confirms make every retry/dead-letter publish wait for the broker,
        // so the original delivery is only acked once its replacement is safe.
        await using var channel =
            await connection.CreateConfirmedChannelAsync(
                stoppingToken);

        // Per consumer (global: false), before subscribing. Automatic recovery
        // re-applies it to the recovered channel before re-registering the
        // consumer; a channel rebuilt by the loop above gets it again here.
        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: PrefetchCount(),
            global: false,
            stoppingToken);

        var lost = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        channel.ChannelShutdownAsync += (_, args) =>
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            if (!connection.IsOpen)
            {
                // The whole connection dropped. Automatic recovery reopens this
                // channel and re-registers the consumer; rebuilding it here too
                // would race recovery and could leave two consumers.
                _logger.LogWarning(
                    "RabbitMQ connection lost: {ReplyText}. Waiting for automatic recovery.",
                    args.ReplyText);

                return Task.CompletedTask;
            }

            _logger.LogError(
                "RabbitMQ channel closed: {ReplyCode} {ReplyText}",
                args.ReplyCode,
                args.ReplyText);

            lost.TrySetResult();
            return Task.CompletedTask;
        };

        channel.CallbackExceptionAsync += (_, args) =>
        {
            _logger.LogError(
                args.Exception,
                "Unhandled exception in RabbitMQ consumer callback.");

            return Task.CompletedTask;
        };

        await _topology.DeclareAsync(channel, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);

        consumer.UnregisteredAsync += (_, _) =>
        {
            // Also raised when the channel shuts down; that case is handled
            // (or deliberately left to recovery) by ChannelShutdownAsync above.
            // Only a broker cancel on a still-open channel is handled here.
            if (stoppingToken.IsCancellationRequested || !channel.IsOpen)
            {
                return Task.CompletedTask;
            }

            _logger.LogError(
                "Consumer subscription to {Queue} was cancelled by the broker.",
                _topology.Queue);

            lost.TrySetResult();
            return Task.CompletedTask;
        };

        consumer.ReceivedAsync += (_, eventArgs) =>
            HandleDeliverySafelyAsync(channel, eventArgs, stoppingToken);

        await channel.BasicConsumeAsync(
            queue: _topology.Queue,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        _logger.LogInformation(
            "Consumer for {Queue} started.",
            _topology.Queue);

        await lost.Task.WaitAsync(stoppingToken);
    }

    /// <summary>
    /// Last line of defence: if a delivery can't be acked, retried or
    /// dead-lettered (e.g. the broker rejects or can't route the publish), put it
    /// back on the queue rather than leaving it unacked or losing it.
    /// </summary>
    private async Task HandleDeliverySafelyAsync(
        IChannel channel,
        BasicDeliverEventArgs eventArgs,
        CancellationToken stoppingToken)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            await HandleDeliveryAsync(channel, eventArgs, started, stoppingToken);
        }
        catch (Exception ex)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                // Shutting down: the channel is about to close, which requeues
                // every unacked delivery anyway.
                return;
            }

            _logger.LogError(
                ex,
                "Could not ack, retry or dead-letter delivery {DeliveryTag}. Requeueing it in {Delay}.",
                eventArgs.DeliveryTag,
                RecoveryDelay);

            try
            {
                await Task.Delay(RecoveryDelay, stoppingToken);

                if (ex is PublishReturnException)
                {
                    // Unroutable: the retry or dead-letter queue was deleted while
                    // we were running. Recreate it so the redelivery can succeed.
                    await _topology.DeclareAsync(channel, stoppingToken);
                }

                await channel.BasicNackAsync(
                    eventArgs.DeliveryTag,
                    multiple: false,
                    requeue: true,
                    stoppingToken);

                RecordConsumed(WaxyCandlesMetrics.ConsumeOutcomes.Requeued, started);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                // Shutting down mid-backoff: the channel close requeues it.
            }
            catch (Exception nackEx)
            {
                // The channel is unusable. When it closes, the broker requeues
                // the delivery and the reconnect loop resubscribes.
                _logger.LogError(
                    nackEx,
                    "Could not requeue delivery {DeliveryTag}; it will be redelivered when the channel closes.",
                    eventArgs.DeliveryTag);
            }
        }
    }

    private async Task HandleDeliveryAsync(
        IChannel channel,
        BasicDeliverEventArgs eventArgs,
        long started,
        CancellationToken stoppingToken)
    {
        var body = eventArgs.Body.ToArray();
        var retryCount = RetryHeaders.GetRetryCount(eventArgs.BasicProperties);

        // Continues the trace from the message headers (a new trace if there
        // are none). Each delivery attempt, retries included, is its own
        // span under the same publish.
        using var activity = WaxyCandlesTelemetry.Source.StartActivity(
            $"process {_topology.Queue}",
            ActivityKind.Consumer,
            MessageTracing.FromHeaders(eventArgs.BasicProperties));

        activity?.SetTag(WaxyCandlesTelemetry.MessagingSystem, "rabbitmq");
        activity?.SetTag(WaxyCandlesTelemetry.MessagingDestination, _topology.Queue);
        activity?.SetTag(WaxyCandlesTelemetry.RetryCount, retryCount);

        var message = TryDeserialize(body, out var unreadableReason);

        if (message is null)
        {
            activity?.SetTag(WaxyCandlesTelemetry.Outcome, "dead-lettered (unreadable)");
            activity?.SetStatus(ActivityStatusCode.Error, "Unreadable message");

            // A message we can't read will never succeed, so retrying it is
            // pointless. Park it in the dead-letter queue for inspection.
            _logger.LogWarning(
                "Received invalid {MessageDescription} message. Sending to dead-letter queue.",
                MessageDescription);

            await PublishToDeadLetterAsync(
                channel,
                body,
                eventArgs.BasicProperties,
                new DeadLetterReason(
                    DeadLetterHeaders.UnreadableReason,
                    unreadableReason,
                    retryCount),
                stoppingToken);

            await channel.BasicAckAsync(
                eventArgs.DeliveryTag,
                multiple: false,
                stoppingToken);

            RecordConsumed(WaxyCandlesMetrics.ConsumeOutcomes.DeadLetteredUnreadable, started);
            return;
        }

        var messageId = GetMessageId(message);
        activity?.SetTag(WaxyCandlesTelemetry.MessagingMessageId, messageId.ToString());

        try
        {
            using var scope = _services.CreateScope();

            var outcome = await ProcessAsync(
                message,
                scope.ServiceProvider,
                stoppingToken);

            await channel.BasicAckAsync(
                eventArgs.DeliveryTag,
                multiple: false,
                stoppingToken);

            activity?.SetTag(WaxyCandlesTelemetry.Outcome, outcome.ToString());

            RecordConsumed(
                outcome switch
                {
                    ConsumeOutcome.AlreadyProcessed => WaxyCandlesMetrics.ConsumeOutcomes.AlreadyProcessed,
                    ConsumeOutcome.Skipped => WaxyCandlesMetrics.ConsumeOutcomes.Skipped,
                    _ => WaxyCandlesMetrics.ConsumeOutcomes.Processed
                },
                started);

            switch (outcome)
            {
                case ConsumeOutcome.AlreadyProcessed:
                    _logger.LogInformation(
                        "Message {MessageId} has already been processed. Skipping.",
                        messageId);
                    break;

                case ConsumeOutcome.Skipped:
                    _logger.LogInformation(
                        "Message {MessageId} skipped.",
                        messageId);
                    break;

                default:
                    _logger.LogInformation(
                        "Successfully processed message {MessageId}.",
                        messageId);
                    break;
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down, not a processing failure: don't consume a retry.
            // The channel close requeues it.
            throw;
        }
        catch (Exception ex)
        {
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            _logger.LogError(
                ex,
                "Error processing message {MessageId}. Retry count: {RetryCount}",
                messageId,
                retryCount);

            if (retryCount >= _topology.MaxRetries)
            {
                _logger.LogError(
                    "Message {MessageId} has reached the maximum retry count. Sending to dead-letter queue.",
                    messageId);

                activity?.SetTag(WaxyCandlesTelemetry.Outcome, "dead-lettered (retries exhausted)");

                await PublishToDeadLetterAsync(
                    channel,
                    body,
                    eventArgs.BasicProperties,
                    // The root cause: e.g. a DbUpdateException's message is
                    // only "See the inner exception".
                    new DeadLetterReason(
                        ex.GetBaseException().GetType().FullName ?? ex.GetBaseException().GetType().Name,
                        ex.GetBaseException().Message,
                        retryCount),
                    stoppingToken);

                await channel.BasicAckAsync(
                    eventArgs.DeliveryTag,
                    multiple: false,
                    stoppingToken);

                RecordConsumed(
                    WaxyCandlesMetrics.ConsumeOutcomes.DeadLetteredRetriesExhausted,
                    started);

                return;
            }

            var nextRetryCount = retryCount + 1;

            activity?.SetTag(
                WaxyCandlesTelemetry.Outcome,
                $"retry {nextRetryCount}/{_topology.MaxRetries} scheduled");

            var headers = new Dictionary<string, object?>
            {
                [RetryHeaders.RetryCount] =
                    RetryHeaders.Encode(nextRetryCount)
            };

            // The retry joins the same trace, under the original publish.
            MessageTracing.CopyHeaders(eventArgs.BasicProperties, headers);

            var properties =
                new BasicProperties
                {
                    Persistent = true,
                    Headers = headers
                };

            // So the retry is still identifiable (MessageId/Type) by whatever
            // reads it, as the original publish was.
            DeadLetterHeaders.CopyMessageProperties(eventArgs.BasicProperties, properties);

            // mandatory: if the retry queue is missing, fail (and requeue the
            // original) rather than have the broker confirm a dropped message.
            await channel.BasicPublishAsync(
                exchange: _topology.RetryExchange,
                routingKey: QueueTopology.RetryRoutingKey,
                mandatory: true,
                basicProperties: properties,
                body: body,
                cancellationToken: stoppingToken);

            await channel.BasicAckAsync(
                eventArgs.DeliveryTag,
                multiple: false,
                stoppingToken);

            RecordConsumed(WaxyCandlesMetrics.ConsumeOutcomes.RetryScheduled, started);

            _logger.LogWarning(
                "Message {MessageId} scheduled for retry {RetryCount}/{MaxRetries}.",
                messageId,
                nextRetryCount,
                _topology.MaxRetries);
        }
    }

    /// <summary>The message, or null (and why, for the dead-letter headers) if it can't be processed.</summary>
    private TMessage? TryDeserialize(byte[] body, out string unreadableReason)
    {
        TMessage? message;

        try
        {
            message = JsonSerializer.Deserialize<TMessage>(body);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "{MessageDescription} message is not valid JSON.",
                MessageDescription);

            unreadableReason = $"Not valid JSON: {ex.Message}";
            return null;
        }

        if (message is null)
        {
            unreadableReason = "The payload is null.";
            return null;
        }

        if (!IsValid(message))
        {
            unreadableReason = "The message is missing required fields or has invalid values.";
            return null;
        }

        unreadableReason = string.Empty;
        return message;
    }

    private ushort PrefetchCount()
    {
        var configured = _services
            .GetService<IConfiguration>()?[PrefetchCountKey];

        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultPrefetchCount;
        }

        // 0 would mean "no limit" to the broker, which is what this avoids.
        // An invalid value must not throw here: that would fail every
        // reconnect attempt and the consumer would never start.
        if (ushort.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var count) &&
            count > 0)
        {
            return count;
        }

        _logger.LogWarning(
            "{Key} must be a whole number from 1 to {Max}; using {Default}.",
            PrefetchCountKey,
            ushort.MaxValue,
            DefaultPrefetchCount);

        return DefaultPrefetchCount;
    }

    // Recorded once the delivery has been acked (or nacked back), so a
    // dead-letter publish that fails and is requeued counts as requeued only.
    // Never throws: it runs after the ack but inside the processing try, where
    // an exception would schedule a retry of a delivery that was already acked.
    private void RecordConsumed(string outcome, long started)
    {
        try
        {
            var queue = WaxyCandlesMetrics.Queue(_topology.Queue);
            var outcomeTag = new KeyValuePair<string, object?>(WaxyCandlesMetrics.OutcomeTag, outcome);

            WaxyCandlesMetrics.MessagesConsumed.Add(1, queue, outcomeTag);
            WaxyCandlesMetrics.MessageProcessingDuration.Record(
                WaxyCandlesMetrics.SecondsSince(started),
                queue,
                outcomeTag);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record consumer metrics for {Queue}.", _topology.Queue);
        }
    }

    private async Task PublishToDeadLetterAsync(
        IChannel channel,
        byte[] body,
        IReadOnlyBasicProperties? original,
        DeadLetterReason reason,
        CancellationToken cancellationToken)
    {
        // Keeps the trace, so a dead-lettered message can be traced back to
        // the price (or alert) that produced it.
        var headers = new Dictionary<string, object?>();
        MessageTracing.CopyHeaders(original, headers);

        // Where it came from and why it failed, for whoever inspects the queue.
        DeadLetterHeaders.Add(headers, _topology.Queue, reason, DateTimeOffset.UtcNow);

        var properties = new BasicProperties { Persistent = true, Headers = headers };
        DeadLetterHeaders.CopyMessageProperties(original, properties);

        // mandatory: if the dead-letter queue is missing, fail (and requeue the
        // original) rather than have the broker confirm a dropped message.
        await channel.BasicPublishAsync(
            exchange: _topology.DeadLetterExchange,
            routingKey: QueueTopology.DeadLetterRoutingKey,
            mandatory: true,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);
    }
}
