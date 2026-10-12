using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

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
    private const int MaxRetries = 2;

    // How long to wait before reopening a lost channel, and before requeueing a
    // delivery whose retry/dead-letter publish failed (avoids a hot redelivery loop).
    private static readonly TimeSpan RecoveryDelay = TimeSpan.FromSeconds(5);

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
        try
        {
            await HandleDeliveryAsync(channel, eventArgs, stoppingToken);
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
        CancellationToken stoppingToken)
    {
        var body = eventArgs.Body.ToArray();

        var message = TryDeserialize(body);

        if (message is null)
        {
            // A message we can't read will never succeed, so retrying it is
            // pointless. Park it in the dead-letter queue for inspection.
            _logger.LogWarning(
                "Received invalid {MessageDescription} message. Sending to dead-letter queue.",
                MessageDescription);

            await PublishToDeadLetterAsync(
                channel,
                body,
                stoppingToken);

            await channel.BasicAckAsync(
                eventArgs.DeliveryTag,
                multiple: false,
                stoppingToken);

            return;
        }

        var messageId = GetMessageId(message);
        var retryCount = RetryHeaders.GetRetryCount(eventArgs.BasicProperties);

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
            _logger.LogError(
                ex,
                "Error processing message {MessageId}. Retry count: {RetryCount}",
                messageId,
                retryCount);

            if (retryCount >= MaxRetries)
            {
                _logger.LogError(
                    "Message {MessageId} has reached the maximum retry count. Sending to dead-letter queue.",
                    messageId);

                await PublishToDeadLetterAsync(
                    channel,
                    body,
                    stoppingToken);

                await channel.BasicAckAsync(
                    eventArgs.DeliveryTag,
                    multiple: false,
                    stoppingToken);

                return;
            }

            var nextRetryCount = retryCount + 1;

            var properties =
                new BasicProperties
                {
                    Persistent = true,
                    Headers = new Dictionary<string, object?>
                    {
                        [RetryHeaders.RetryCount] =
                            RetryHeaders.Encode(nextRetryCount)
                    }
                };

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

            _logger.LogWarning(
                "Message {MessageId} scheduled for retry {RetryCount}/{MaxRetries}.",
                messageId,
                nextRetryCount,
                MaxRetries);
        }
    }

    private TMessage? TryDeserialize(byte[] body)
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

            return null;
        }

        return message is not null && IsValid(message)
            ? message
            : null;
    }

    private async Task PublishToDeadLetterAsync(
        IChannel channel,
        byte[] body,
        CancellationToken cancellationToken)
    {
        // mandatory: if the dead-letter queue is missing, fail (and requeue the
        // original) rather than have the broker confirm a dropped message.
        await channel.BasicPublishAsync(
            exchange: _topology.DeadLetterExchange,
            routingKey: QueueTopology.DeadLetterRoutingKey,
            mandatory: true,
            basicProperties: new BasicProperties { Persistent = true },
            body: body,
            cancellationToken: cancellationToken);
    }
}
