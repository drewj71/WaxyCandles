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

    public const string OutboxPublishesNonPersistentMessages =
        "Known bug: OutboxPublisherWorker publishes without Persistent = true, then marks " +
        "the row processed, so a RabbitMQ restart silently loses those messages.";

    public const string AuthFailuresReturn500 =
        "Known bug: AuthService throws plain Exception for bad credentials, duplicate " +
        "users, weak passwords and revoked refresh tokens, which ExceptionHandlingMiddleware " +
        "turns into 500 instead of a 4xx.";

    public const string ConfirmPasswordIsIgnored =
        "Known bug: RegisterRequest.ConfirmPassword is never checked, so registration " +
        "succeeds when it doesn't match Password.";
}
