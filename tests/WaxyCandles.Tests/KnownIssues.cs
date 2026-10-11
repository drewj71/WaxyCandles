namespace WaxyCandles.Tests;

/// <summary>
/// Skip reasons for tests that describe the correct behavior but currently fail
/// because of a known bug. Remove the Skip when the bug is fixed.
/// </summary>
public static class KnownIssues
{
    public const string ProcessedMessageCheckIsNotAtomic =
        "Known bug: StockPriceConsumer checks ProcessedMessages before evaluating and " +
        "inserts afterwards, so concurrent deliveries of one message both evaluate alerts.";

    public const string MalformedPayloadIsNeverAcked =
        "Known bug: StockPriceConsumer deserializes outside its try/catch, so invalid " +
        "JSON throws before ack/retry and the message is left unacked indefinitely.";
}
