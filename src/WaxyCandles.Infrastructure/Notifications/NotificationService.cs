using System.Globalization;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.Alerts.Messages;
using WaxyCandles.Application.Notifications.DTOs;
using WaxyCandles.Application.Notifications.Interfaces;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.Persistence;

namespace WaxyCandles.Infrastructure.Notifications;

public class NotificationService : INotificationService
{
    // Named by EF's convention in the AddNotifications migration.
    private const string RecipientForeignKey = "FK_Notifications_AspNetUsers_UserId";

    private readonly AppDbContext _dbContext;

    public NotificationService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<CreateNotificationResult> CreateForTriggeredAlertAsync(
        AlertTriggeredMessage message,
        CancellationToken cancellationToken = default)
    {
        // Fast path for redeliveries; the unique index on SourceMessageId is the
        // actual guarantee if two deliveries race past this check.
        var exists = await _dbContext.Notifications
            .AnyAsync(
                x => x.SourceMessageId == message.MessageId,
                cancellationToken);

        if (exists)
        {
            return new CreateNotificationResult(CreateNotificationStatus.Duplicate);
        }

        var verb = message.Direction == AlertDirection.Above
            ? "rose above"
            : "fell below";

        // Level is null for price alerts, including messages written before
        // level alerts existed.
        var (title, body) = message.Level is { } level
            ? ($"{message.Symbol} GEX level alert triggered",
               $"{message.Symbol} {verb} {FormatPrice(message.TargetPrice)}, approaching its " +
               LevelName(level) +
               (message.LevelPrice is { } levelPrice ? $" at {FormatPrice(levelPrice)}" : "") +
               $" (now {FormatPrice(message.TriggerPrice)}).")
            : ($"{message.Symbol} alert triggered",
               $"{message.Symbol} {verb} {FormatPrice(message.TargetPrice)} " +
               $"(now {FormatPrice(message.TriggerPrice)}).");

        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            UserId = message.UserId,
            Type = NotificationType.AlertTriggered,
            Title = title,
            Body = body,
            AlertId = message.AlertId,
            SourceMessageId = message.MessageId,
            CreatedAt = message.TriggeredAt
        };

        _dbContext.Notifications.Add(notification);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
            when (ex.IsUniqueConstraintViolation())
        {
            return new CreateNotificationResult(CreateNotificationStatus.Duplicate);
        }
        catch (DbUpdateException ex)
            when (ex.IsForeignKeyViolation(RecipientForeignKey))
        {
            // Alerts have no foreign key to users, so an alert can outlive its
            // user. Retrying would fail the same way every time.
            return new CreateNotificationResult(CreateNotificationStatus.RecipientNotFound);
        }

        return new CreateNotificationResult(
            CreateNotificationStatus.Created,
            ToResponse(notification));
    }

    public async Task<NotificationListResponse> GetRecentAsync(
        string userId,
        int take,
        CancellationToken cancellationToken = default)
    {
        var items = await _dbContext.Notifications
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(take)
            .Select(x => new NotificationResponse
            {
                Id = x.Id,
                Type = x.Type,
                Title = x.Title,
                Body = x.Body,
                AlertId = x.AlertId,
                CreatedAt = x.CreatedAt,
                ReadAt = x.ReadAt
            })
            .ToListAsync(cancellationToken);

        var unreadCount = await _dbContext.Notifications
            .CountAsync(
                x => x.UserId == userId && x.ReadAt == null,
                cancellationToken);

        return new NotificationListResponse
        {
            Items = items,
            UnreadCount = unreadCount
        };
    }

    public async Task<bool> MarkReadAsync(
        Guid id,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var notification = await _dbContext.Notifications
            .FirstOrDefaultAsync(
                x => x.Id == id && x.UserId == userId,
                cancellationToken);

        if (notification is null)
        {
            return false;
        }

        notification.ReadAt ??= DateTimeOffset.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task MarkAllReadAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        await _dbContext.Notifications
            .Where(x => x.UserId == userId && x.ReadAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.ReadAt, now),
                cancellationToken);
    }

    // "$150.25", "$0.0525": at least 2 decimals, up to the 6 stored.
    private static string FormatPrice(decimal price) =>
        "$" + price.ToString("0.00####", CultureInfo.InvariantCulture);

    private static string LevelName(GexLevelKind level) => level switch
    {
        GexLevelKind.King => "King node",
        GexLevelKind.Gatekeeper => "gatekeeper",
        GexLevelKind.Floor => "floor",
        GexLevelKind.Ceiling => "ceiling",
        _ => "GEX level"
    };

    private static NotificationResponse ToResponse(Notification notification) => new()
    {
        Id = notification.Id,
        Type = notification.Type,
        Title = notification.Title,
        Body = notification.Body,
        AlertId = notification.AlertId,
        CreatedAt = notification.CreatedAt,
        ReadAt = notification.ReadAt
    };
}
