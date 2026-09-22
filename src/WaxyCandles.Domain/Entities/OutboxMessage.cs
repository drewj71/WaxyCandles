namespace WaxyCandles.Domain.Entities;

public class OutboxMessage
{
    public Guid Id { get; set; }

    public string Type { get; set; } = default!;

    public string Payload { get; set; } = default!;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public string? Error { get; set; }
}