using System.Globalization;
using System.Text;
using RabbitMQ.Client;

namespace WaxyCandles.Infrastructure.Messaging;

public static class RetryHeaders
{
    public const string RetryCount = "x-retry-count";

    /// <summary>
    /// How many times this delivery has already been retried. RabbitMQ delivers
    /// string header values as byte[], which is also how consumers write it.
    /// </summary>
    public static int GetRetryCount(
        IReadOnlyBasicProperties? properties)
    {
        if (properties?.Headers is null)
            return 0;

        if (!properties.Headers.TryGetValue(
                RetryCount,
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

    public static byte[] Encode(int retryCount) =>
        Encoding.UTF8.GetBytes(retryCount.ToString());
}

/// <summary>Why a message was dead-lettered, recorded in its headers.</summary>
/// <param name="Type">The exception type, or <see cref="DeadLetterHeaders.UnreadableReason"/>.</param>
/// <param name="Message">The exception message, or what was wrong with the payload.</param>
/// <param name="RetryCount">Retries already made when it was dead-lettered.</param>
public sealed record DeadLetterReason(string Type, string Message, int RetryCount);

/// <summary>
/// What retry and dead-letter publishes carry besides the body: the original's
/// identifying properties and, on a dead-letter, where and why it failed.
/// Header values are written as UTF-8 bytes, the way RabbitMQ delivers string
/// headers anyway (and the way <see cref="RetryHeaders"/> reads them).
/// </summary>
public static class DeadLetterHeaders
{
    public const string OriginalQueue = "x-original-queue";
    public const string ExceptionType = "x-exception-type";
    public const string ExceptionMessage = "x-exception-message";
    public const string FailedAt = "x-failed-at";

    /// <summary>
    /// Retries already made when it was dead-lettered. Informational only, and
    /// deliberately not <see cref="RetryHeaders.RetryCount"/>: a dead-letter
    /// carries no live retry count, so a message moved back to its queue (UI
    /// "move messages", shovel) gets a full retry budget again.
    /// </summary>
    public const string RetriesAttempted = "x-dead-letter-retry-count";

    /// <summary><see cref="ExceptionType"/> for a payload that couldn't be read or failed validation.</summary>
    public const string UnreadableReason = "unreadable";

    /// <summary>Longer exception messages are cut to this many characters.</summary>
    public const int MaxExceptionMessageLength = 1024;

    public static void Add(
        IDictionary<string, object?> headers,
        string originalQueue,
        DeadLetterReason reason,
        DateTimeOffset failedAt)
    {
        headers[OriginalQueue] = Encode(originalQueue);
        headers[ExceptionType] = Encode(reason.Type);
        headers[ExceptionMessage] = Encode(Truncate(reason.Message));
        headers[FailedAt] = Encode(
            failedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

        // Never RetryHeaders.RetryCount; see RetriesAttempted.
        headers[RetriesAttempted] = Encode(
            reason.RetryCount.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Copies the properties that identify a message (MessageId, Type,
    /// ContentType, CorrelationId) from a delivery onto its republish. Only
    /// those the original has are set.
    /// </summary>
    public static void CopyMessageProperties(
        IReadOnlyBasicProperties? from,
        BasicProperties to)
    {
        if (from is null)
        {
            return;
        }

        if (from.IsMessageIdPresent())
        {
            to.MessageId = from.MessageId;
        }

        if (from.IsTypePresent())
        {
            to.Type = from.Type;
        }

        if (from.IsContentTypePresent())
        {
            to.ContentType = from.ContentType;
        }

        if (from.IsCorrelationIdPresent())
        {
            to.CorrelationId = from.CorrelationId;
        }
    }

    private static string Truncate(string value) =>
        value.Length <= MaxExceptionMessageLength
            ? value
            : value[..MaxExceptionMessageLength];

    private static byte[] Encode(string value) => Encoding.UTF8.GetBytes(value);
}
