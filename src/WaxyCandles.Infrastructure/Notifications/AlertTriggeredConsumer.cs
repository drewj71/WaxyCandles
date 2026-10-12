using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WaxyCandles.Application.Alerts.Messages;
using WaxyCandles.Application.Notifications.DTOs;
using WaxyCandles.Application.Notifications.Interfaces;
using WaxyCandles.Infrastructure.Messaging;

namespace WaxyCandles.Infrastructure.Notifications;

/// <summary>
/// Turns each triggered alert into a stored notification, then pushes it to the
/// user's connected clients. Hosted by the API, where those clients connect.
/// </summary>
public class AlertTriggeredConsumer : RabbitMqConsumer<AlertTriggeredMessage>
{
    private readonly ILogger<AlertTriggeredConsumer> _logger;

    public AlertTriggeredConsumer(
        IServiceProvider services,
        ILogger<AlertTriggeredConsumer> logger)
        : base(QueueTopology.AlertTriggered, services, logger)
    {
        _logger = logger;
    }

    protected override string MessageDescription => "alert triggered";

    protected override Guid GetMessageId(AlertTriggeredMessage message) =>
        message.MessageId;

    protected override bool IsValid(AlertTriggeredMessage message) =>
        message.MessageId != Guid.Empty &&
        message.AlertId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(message.UserId) &&
        !string.IsNullOrWhiteSpace(message.Symbol);

    protected override async Task<ConsumeOutcome> ProcessAsync(
        AlertTriggeredMessage message,
        IServiceProvider scopedServices,
        CancellationToken cancellationToken)
    {
        var notifications =
            scopedServices.GetRequiredService<INotificationService>();

        var result =
            await notifications.CreateForTriggeredAlertAsync(
                message,
                cancellationToken);

        switch (result.Status)
        {
            case CreateNotificationStatus.Duplicate:
                // A redelivery: the notification exists and was pushed (or
                // attempted) the first time. Don't push it again.
                return ConsumeOutcome.AlreadyProcessed;

            case CreateNotificationStatus.RecipientNotFound:
                _logger.LogWarning(
                    "Alert {AlertId} triggered for user {UserId}, who no longer exists. No notification created.",
                    message.AlertId,
                    message.UserId);

                return ConsumeOutcome.Skipped;
        }

        var notification = result.Notification!;

        // Best effort, after the notification is stored: a failed push must not
        // fail (and retry) the message, or it would be retried as a duplicate.
        try
        {
            await scopedServices
                .GetRequiredService<IRealtimeNotifier>()
                .NotifyAsync(message.UserId, notification, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Could not push notification {NotificationId} to user {UserId}; it is stored and will show on their next fetch.",
                notification.Id,
                message.UserId);
        }

        return ConsumeOutcome.Processed;
    }
}
