using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Application.Alerts.DTOs;
using WaxyCandles.Application.Alerts.Interfaces;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Messaging;
using WaxyCandles.Infrastructure.Telemetry;

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

    // A missing Price deserializes to 0, which would trigger every "Below"
    // alert on the symbol, and a missing Timestamp would defeat the guard
    // against out-of-order prices, so both count as unreadable.
    protected override bool IsValid(StockPriceUpdatedMessage message) =>
        message.MessageId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(message.Symbol) &&
        message.Price > 0 &&
        message.Timestamp != default;

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

        // Transient SQL errors re-run the whole attempt, from the fast path to
        // the commit (EF's retrying execution strategy; the evaluation inside
        // joins this attempt rather than retrying on its own). Once the retries
        // are used up the error reaches RabbitMqConsumer: queue retry, then
        // dead-letter, as before.
        var strategy = dbContext.Database.CreateExecutionStrategy();

        var (outcome, evaluation) = await strategy.ExecuteAsync(
            ct => ProcessOnceAsync(message, dbContext, alertEvaluationService, ct),
            cancellationToken);

        // Only the attempt that committed gets here, once: a failed attempt's
        // fires were rolled back and its result discarded with it. After an
        // ambiguous commit the replay finds the claim and returns None, so
        // that fire is not counted (rare under-count, never an over-count).
        WaxyCandlesMetrics.RecordAlertsFired(evaluation);

        return outcome;
    }

    /// <summary>
    /// One attempt. Safe to run again from the start: nothing read or tracked
    /// by a failed attempt is reused.
    /// </summary>
    /// <remarks>
    /// After an ambiguous commit (it reached the database, but the client saw
    /// a transient error) the next attempt finds this message's claim and
    /// returns <see cref="ConsumeOutcome.AlreadyProcessed"/>, so the alerts are
    /// not evaluated, fired or written to the outbox a second time.
    /// </remarks>
    private static async Task<(ConsumeOutcome Outcome, AlertEvaluationResult Evaluation)> ProcessOnceAsync(
        StockPriceUpdatedMessage message,
        AppDbContext dbContext,
        IAlertEvaluationService alertEvaluationService,
        CancellationToken cancellationToken)
    {
        // A failed attempt's claim and outbox rows were rolled back in the
        // database but are still tracked (Added, or Unchanged if saved before
        // the rollback). The context belongs to this message's scope only.
        dbContext.ChangeTracker.Clear();

        // Fast path for ordinary redeliveries. Not a guarantee on its own:
        // the claim below is what ensures a message is evaluated only once.
        var alreadyProcessed =
            await dbContext.ProcessedMessages
                .AnyAsync(
                    x => x.Id == message.MessageId,
                    cancellationToken);

        if (alreadyProcessed)
        {
            return (ConsumeOutcome.AlreadyProcessed, AlertEvaluationResult.None);
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
            return (ConsumeOutcome.AlreadyProcessed, AlertEvaluationResult.None);
        }

        var evaluation = await alertEvaluationService.EvaluateAsync(
            message.Symbol,
            message.Price,
            message.Timestamp,
            cancellationToken);

        await transaction.CommitAsync(
            cancellationToken);

        return (ConsumeOutcome.Processed, evaluation);
    }
}
