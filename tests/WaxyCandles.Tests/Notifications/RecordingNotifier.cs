using System.Collections.Concurrent;
using WaxyCandles.Application.Notifications.DTOs;
using WaxyCandles.Application.Notifications.Interfaces;

namespace WaxyCandles.Tests.Notifications;

/// <summary>Records pushes instead of sending them; can be told to fail or hang.</summary>
public sealed class RecordingNotifier : IRealtimeNotifier
{
    private readonly ConcurrentQueue<(string UserId, NotificationResponse Notification)> _pushes = new();

    public IReadOnlyCollection<(string UserId, NotificationResponse Notification)> Pushes =>
        _pushes.ToArray();

    public bool Fail { get; set; }

    /// <summary>Never completes until cancelled, like a stalled client connection.</summary>
    public bool Hang { get; set; }

    public async Task NotifyAsync(
        string userId,
        NotificationResponse notification,
        CancellationToken cancellationToken = default)
    {
        if (Fail)
        {
            throw new InvalidOperationException("Simulated push failure");
        }

        if (Hang)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        _pushes.Enqueue((userId, notification));
    }
}
