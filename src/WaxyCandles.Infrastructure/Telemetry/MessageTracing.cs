using System.Diagnostics;
using System.Text;
using RabbitMQ.Client;
using WaxyCandles.Domain.Entities;

namespace WaxyCandles.Infrastructure.Telemetry;

/// <summary>
/// Carries the W3C trace context (traceparent/tracestate) across the two
/// hand-offs that aren't calls: the outbox row, and the RabbitMQ message
/// headers. Reading never fails: a missing or malformed value just means the
/// receiver starts a new trace, so tracing can't break message processing.
/// </summary>
public static class MessageTracing
{
    public const string TraceParentHeader = "traceparent";
    public const string TraceStateHeader = "tracestate";

    // traceparent is always 55 characters; tracestate is capped at 32 entries
    // by the spec, which fits in 512.
    public const int TraceParentMaxLength = 55;
    public const int TraceStateMaxLength = 512;

    /// <summary>Saves the current trace context, if any, in an outbox row written in this span.</summary>
    public static void Stamp(OutboxMessage message)
    {
        var current = Activity.Current;

        if (current is null || current.IdFormat != ActivityIdFormat.W3C)
        {
            return;
        }

        message.TraceParent = current.Id;
        message.TraceState = current.TraceStateString is { Length: <= TraceStateMaxLength } state
            ? state
            : null;
    }

    /// <summary>The trace context saved in an outbox row, or default if none.</summary>
    public static ActivityContext FromOutbox(OutboxMessage message) =>
        Parse(message.TraceParent, message.TraceState);

    /// <summary>The trace context in a delivery's headers, or default if none.</summary>
    public static ActivityContext FromHeaders(IReadOnlyBasicProperties? properties) =>
        Parse(
            HeaderString(properties, TraceParentHeader),
            HeaderString(properties, TraceStateHeader));

    /// <summary>Adds the current span's context to outgoing message headers.</summary>
    public static void InjectCurrent(IDictionary<string, object?> headers)
    {
        var current = Activity.Current;

        if (current is null || current.IdFormat != ActivityIdFormat.W3C)
        {
            return;
        }

        headers[TraceParentHeader] = Encoding.UTF8.GetBytes(current.Id!);

        if (!string.IsNullOrEmpty(current.TraceStateString))
        {
            headers[TraceStateHeader] = Encoding.UTF8.GetBytes(current.TraceStateString);
        }
    }

    /// <summary>
    /// Copies a delivery's trace headers, unchanged, onto a republish (retry or
    /// dead-letter), so the next attempt joins the same trace as a sibling of
    /// this one rather than starting a new trace.
    /// </summary>
    public static void CopyHeaders(
        IReadOnlyBasicProperties? from,
        IDictionary<string, object?> to)
    {
        if (from?.Headers is null)
        {
            return;
        }

        foreach (var name in (string[])[TraceParentHeader, TraceStateHeader])
        {
            if (from.Headers.TryGetValue(name, out var value) && value is not null)
            {
                to[name] = value;
            }
        }
    }

    private static ActivityContext Parse(string? traceParent, string? traceState) =>
        traceParent is not null &&
        ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var context)
            ? context
            : default;

    // RabbitMQ delivers string header values as byte[].
    private static string? HeaderString(IReadOnlyBasicProperties? properties, string name) =>
        properties?.Headers is not null &&
        properties.Headers.TryGetValue(name, out var value)
            ? value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string text => text,
                _ => null
            }
            : null;
}
