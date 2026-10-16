using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WaxyCandles.Application.Alerts.Messages;
using WaxyCandles.Application.Notifications.DTOs;
using WaxyCandles.Application.Notifications.Interfaces;
using WaxyCandles.Infrastructure.Messaging;
using WaxyCandles.Infrastructure.Telemetry;

namespace WaxyCandles.Infrastructure.Notifications;

/// <summary>
/// Turns each triggered alert into a stored notification, then pushes it to the
/// user's connected clients. Hosted by the API, where those clients connect.
/// </summary>
public class AlertTriggeredConsumer : RabbitMqConsumer<AlertTriggeredMessage>
{
    // The push happens before the delivery is acked, and deliveries are handled
    // one at a time, so a slow client connection must not hold up the queue.
    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(5);

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
        !string.IsNullOrWhiteSpace(message.Symbol) &&
        Enum.IsDefined(message.Direction);

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

        // Best effort, after the notification is stored: a failed or slow push
        // must not fail (and retry) the message, which would only find a duplicate.
        using var pushTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pushTimeout.CancelAfter(PushTimeout);

        using var activity = WaxyCandlesTelemetry.Source.StartActivity("signalr push");

        try
        {
            await scopedServices
                .GetRequiredService<IRealtimeNotifier>()
                .NotifyAsync(message.UserId, notification, pushTimeout.Token);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            activity?.AddException(ex);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "Push failed");

            // Includes the push timing out. Only a shutdown propagates.
            _logger.LogWarning(
                ex,
                "Could not push notification {NotificationId} to user {UserId}; it is stored and will show on their next fetch.",
                notification.Id,
                message.UserId);
        }

        return ConsumeOutcome.Processed;
    }
}
