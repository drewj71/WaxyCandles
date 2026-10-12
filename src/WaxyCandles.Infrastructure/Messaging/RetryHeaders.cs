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
