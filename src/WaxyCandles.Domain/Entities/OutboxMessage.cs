namespace WaxyCandles.Domain.Entities;

public class OutboxMessage
{
    public Guid Id { get; set; }

    public string Type { get; set; } = default!;

    public string Payload { get; set; } = default!;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public string? Error { get; set; }

    /// <summary>
    /// The W3C trace context of the span that wrote this row, so the publish
    /// and everything downstream join that trace. Null when tracing is off,
    /// and for rows written before tracing existed.
    /// </summary>
    public string? TraceParent { get; set; }

    public string? TraceState { get; set; }
}