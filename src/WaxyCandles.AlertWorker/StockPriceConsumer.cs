using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Application.Alerts.Interfaces;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Messaging;

namespace WaxyCandles.AlertWorker;

public class StockPriceConsumer : BackgroundService
{
    private const int MaxRetries = 2;

    // How long to wait before reopening a lost channel, and before requeueing a
    // delivery whose retry/dead-letter publish failed (avoids a hot redelivery loop).
    private static readonly TimeSpan RecoveryDelay = TimeSpan.FromSeconds(5);

    private readonly ILogger<StockPriceConsumer> _logger;
    private readonly RabbitMqConnection _rabbitMqConnection;
    private readonly IServiceScopeFactory _scopeFactory;

    public StockPriceConsumer(
        IServiceScopeFactory scopeFactory,
        RabbitMqConnection rabbitMqConnection,
        ILogger<StockPriceConsumer> logger)
    {
        _scopeFactory = scopeFactory;
        _rabbitMqConnection = rabbitMqConnection;
        _logger = logger;
    }

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
                    "Stock price consumer failed.");
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
        // Confirms make every retry/dead-letter publish wait for the broker,
        // so the original delivery is only acked once its replacement is safe.
        await using var channel =
            await _rabbitMqConnection.CreateConfirmedChannelAsync(
                stoppingToken);

        var lost = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        channel.ChannelShutdownAsync += (_, args) =>
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            if (!_rabbitMqConnection.IsOpen)
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

        await DeclareTopologyAsync(channel, stoppingToken);

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
                "Consumer subscription to stock-price-updated was cancelled by the broker.");

            lost.TrySetResult();
            return Task.CompletedTask;
        };

        consumer.ReceivedAsync += (_, eventArgs) =>
            HandleDeliverySafelyAsync(channel, eventArgs, stoppingToken);

        await channel.BasicConsumeAsync(
            queue: "stock-price-updated",
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        _logger.LogInformation(
            "Stock price consumer started.");

        await lost.Task.WaitAsync(stoppingToken);
    }

    private static async Task DeclareTopologyAsync(
        IChannel channel,
        CancellationToken stoppingToken)
    {
        await channel.ExchangeDeclareAsync(
            exchange: "stock-price-retry",
            type: "direct",
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: stoppingToken);

        await channel.ExchangeDeclareAsync(
            exchange: "stock-price-dlx",
            type: "direct",
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: stoppingToken);

        await channel.QueueDeclareAsync(
            queue: "stock-price-retry",
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = 5000,
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = "stock-price-updated"
            },
            cancellationToken: stoppingToken);

        await channel.QueueBindAsync(
            queue: "stock-price-retry",
            exchange: "stock-price-retry",
            routingKey: "retry",
            cancellationToken: stoppingToken);

        await channel.QueueDeclareAsync(
            queue: "stock-price-dead-letter",
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: stoppingToken);

        await channel.QueueBindAsync(
            queue: "stock-price-dead-letter",
            exchange: "stock-price-dlx",
            routingKey: "dead-letter",
            cancellationToken: stoppingToken);

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
                    await DeclareTopologyAsync(channel, stoppingToken);
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
                "Received invalid stock price message. Sending to dead-letter queue.");

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

        _logger.LogInformation(
            "Received stock price update: {Symbol} = {Price}",
            message.Symbol,
            message.Price);

        var retryCount = GetRetryCount(eventArgs.BasicProperties);

        try
        {
            using var scope = _scopeFactory.CreateScope();

            var dbContext =
                scope.ServiceProvider
                    .GetRequiredService<AppDbContext>();

            // Resolved from the same scope, so it shares dbContext and
            // therefore the transaction below.
            var alertEvaluationService =
                scope.ServiceProvider
                    .GetRequiredService<IAlertEvaluationService>();

            // Fast path for ordinary redeliveries. Not a guarantee on its own:
            // the claim below is what ensures a message is evaluated only once.
            var alreadyProcessed =
                await dbContext.ProcessedMessages
                    .AnyAsync(
                        x => x.Id == message.MessageId,
                        stoppingToken);

            if (alreadyProcessed)
            {
                await AckAlreadyProcessedAsync(
                    channel,
                    eventArgs.DeliveryTag,
                    message.MessageId,
                    stoppingToken);

                return;
            }

            await using var transaction =
                await dbContext.Database.BeginTransactionAsync(
                    stoppingToken);

            // Claim the message before evaluating. The primary key makes the
            // claim atomic: a concurrent delivery of the same message blocks on
            // this insert until we commit, then fails with a duplicate key.
            // If evaluation fails, the rollback releases the claim for a retry.
            dbContext.ProcessedMessages.Add(
                new ProcessedMessage
                {
                    Id = message.MessageId,
                    ProcessedAt = DateTimeOffset.UtcNow
                });

            try
            {
                await dbContext.SaveChangesAsync(
                    stoppingToken);
            }
            catch (DbUpdateException ex)
                when (ex.IsUniqueConstraintViolation())
            {
                await AckAlreadyProcessedAsync(
                    channel,
                    eventArgs.DeliveryTag,
                    message.MessageId,
                    stoppingToken);

                return;
            }

            await alertEvaluationService.EvaluateAsync(
                message.Symbol,
                message.Price,
                stoppingToken);

            await transaction.CommitAsync(
                stoppingToken);

            await channel.BasicAckAsync(
                eventArgs.DeliveryTag,
                multiple: false,
                stoppingToken);

            _logger.LogInformation(
                "Successfully processed message {MessageId}.",
                message.MessageId);
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down, not a processing failure: don't consume a retry.
            // The transaction has rolled back and the channel close requeues it.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error processing message {MessageId}. Retry count: {RetryCount}",
                message.MessageId,
                retryCount);

            if (retryCount >= MaxRetries)
            {
                _logger.LogError(
                    "Message {MessageId} has reached the maximum retry count. Sending to dead-letter queue.",
                    message.MessageId);

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
                        ["x-retry-count"] =
                            Encoding.UTF8.GetBytes(
                                nextRetryCount.ToString())
                    }
                };

            // mandatory: if the retry queue is missing, fail (and requeue the
            // original) rather than have the broker confirm a dropped message.
            await channel.BasicPublishAsync(
                exchange: "stock-price-retry",
                routingKey: "retry",
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
                message.MessageId,
                nextRetryCount,
                MaxRetries);
        }
    }

    private async Task AckAlreadyProcessedAsync(
        IChannel channel,
        ulong deliveryTag,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Message {MessageId} has already been processed. Skipping.",
            messageId);

        await channel.BasicAckAsync(
            deliveryTag,
            multiple: false,
            cancellationToken);
    }

    private StockPriceUpdatedMessage? TryDeserialize(byte[] body)
    {
        StockPriceUpdatedMessage? message;

        try
        {
            message =
                JsonSerializer.Deserialize<StockPriceUpdatedMessage>(
                    body);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Stock price message is not valid JSON.");

            return null;
        }

        // A missing Price deserializes to 0, which would trigger every
        // "Below" alert on the symbol, so it counts as unreadable too.
        if (message is null ||
            message.MessageId == Guid.Empty ||
            string.IsNullOrWhiteSpace(message.Symbol) ||
            message.Price <= 0)
        {
            return null;
        }

        return message;
    }

    private static async Task PublishToDeadLetterAsync(
        IChannel channel,
        byte[] body,
        CancellationToken cancellationToken)
    {
        // mandatory: if the dead-letter queue is missing, fail (and requeue the
        // original) rather than have the broker confirm a dropped message.
        await channel.BasicPublishAsync(
            exchange: "stock-price-dlx",
            routingKey: "dead-letter",
            mandatory: true,
            basicProperties: new BasicProperties { Persistent = true },
            body: body,
            cancellationToken: cancellationToken);
    }

    internal static int GetRetryCount(
        IReadOnlyBasicProperties? properties)
    {
        if (properties?.Headers is null)
            return 0;

        if (!properties.Headers.TryGetValue(
                "x-retry-count",
                out var value))
        {
            return 0;
        }

        if (value is byte[] bytes &&
            int.TryParse(
                Encoding.UTF8.GetString(bytes),
                out var count))
        {
            return count;
        }

        return 0;
    }
}
