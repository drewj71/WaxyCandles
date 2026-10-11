using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Application.Alerts.Interfaces;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Messaging;

namespace WaxyCandles.AlertWorker;

public class StockPriceConsumer : BackgroundService
{
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
        // Confirms make every retry/dead-letter publish wait for the broker,
        // so the original delivery is only acked once its replacement is safe.
        await using var channel =
            await _rabbitMqConnection.CreateConfirmedChannelAsync(
                stoppingToken);

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

        var consumer = new AsyncEventingBasicConsumer(channel);

        consumer.ReceivedAsync += async (_, eventArgs) =>
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
                    multiple: false);

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
                        message.MessageId);

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
                        message.MessageId);

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
                    multiple: false);

                _logger.LogInformation(
                    "Successfully processed message {MessageId}.",
                    message.MessageId);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing message {MessageId}. Retry count: {RetryCount}",
                    message.MessageId,
                    retryCount);

                if (retryCount >= 2)
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
                        multiple: false);

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

                await channel.BasicPublishAsync(
                    exchange: "stock-price-retry",
                    routingKey: "retry",
                    mandatory: false,
                    basicProperties: properties,
                    body: body,
                    cancellationToken: stoppingToken);

                await channel.BasicAckAsync(
                    eventArgs.DeliveryTag,
                    multiple: false);

                _logger.LogWarning(
                    "Message {MessageId} scheduled for retry {RetryCount}/3.",
                    message.MessageId,
                    nextRetryCount);
            }
        };

        await channel.BasicConsumeAsync(
            queue: "stock-price-updated",
            autoAck: false,
            consumer: consumer);

        _logger.LogInformation(
            "Stock price consumer started.");

        await Task.Delay(
            Timeout.Infinite,
            stoppingToken);
    }

    private async Task AckAlreadyProcessedAsync(
        IChannel channel,
        ulong deliveryTag,
        Guid messageId)
    {
        _logger.LogInformation(
            "Message {MessageId} has already been processed. Skipping.",
            messageId);

        await channel.BasicAckAsync(
            deliveryTag,
            multiple: false);
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

        if (message is null ||
            message.MessageId == Guid.Empty ||
            string.IsNullOrWhiteSpace(message.Symbol))
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
        await channel.BasicPublishAsync(
            exchange: "stock-price-dlx",
            routingKey: "dead-letter",
            mandatory: false,
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