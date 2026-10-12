using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Application.Alerts.Interfaces;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Messaging;

namespace WaxyCandles.AlertWorker;

/// <summary>
/// Evaluates alerts for each price update. Transport, retry and dead-lettering
/// live in <see cref="RabbitMqConsumer{TMessage}"/>.
/// </summary>
public class StockPriceConsumer : RabbitMqConsumer<StockPriceUpdatedMessage>
{
    private readonly ILogger<StockPriceConsumer> _logger;

    public StockPriceConsumer(
        IServiceProvider services,
        ILogger<StockPriceConsumer> logger)
        : base(QueueTopology.StockPriceUpdated, services, logger)
    {
        _logger = logger;
    }

    protected override string MessageDescription => "stock price";

    protected override Guid GetMessageId(StockPriceUpdatedMessage message) =>
        message.MessageId;

    // A missing Price deserializes to 0, which would trigger every
    // "Below" alert on the symbol, so it counts as unreadable too.
    protected override bool IsValid(StockPriceUpdatedMessage message) =>
        message.MessageId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(message.Symbol) &&
        message.Price > 0;

    protected override async Task<ConsumeOutcome> ProcessAsync(
        StockPriceUpdatedMessage message,
        IServiceProvider scopedServices,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Received stock price update: {Symbol} = {Price}",
            message.Symbol,
            message.Price);

        var dbContext =
            scopedServices.GetRequiredService<AppDbContext>();

        // Resolved from the same scope, so it shares dbContext and therefore the
        // transaction below (including the AlertTriggered outbox rows it adds).
        var alertEvaluationService =
            scopedServices.GetRequiredService<IAlertEvaluationService>();

        // Fast path for ordinary redeliveries. Not a guarantee on its own:
        // the claim below is what ensures a message is evaluated only once.
        var alreadyProcessed =
            await dbContext.ProcessedMessages
                .AnyAsync(
                    x => x.Id == message.MessageId,
                    cancellationToken);

        if (alreadyProcessed)
        {
            return ConsumeOutcome.AlreadyProcessed;
        }

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(
                cancellationToken);

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
                cancellationToken);
        }
        catch (DbUpdateException ex)
            when (ex.IsUniqueConstraintViolation())
        {
            return ConsumeOutcome.AlreadyProcessed;
        }

        await alertEvaluationService.EvaluateAsync(
            message.Symbol,
            message.Price,
            cancellationToken);

        await transaction.CommitAsync(
            cancellationToken);

        return ConsumeOutcome.Processed;
    }
}
