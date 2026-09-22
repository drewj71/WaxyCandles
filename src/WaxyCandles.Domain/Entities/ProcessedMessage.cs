namespace WaxyCandles.Domain.Entities;

public class ProcessedMessage
{
    public Guid Id { get; set; }

    public DateTimeOffset ProcessedAt { get; set; }
}