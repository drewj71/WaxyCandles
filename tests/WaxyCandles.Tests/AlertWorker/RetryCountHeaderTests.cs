using System.Text;
using RabbitMQ.Client;
using WaxyCandles.AlertWorker;

namespace WaxyCandles.Tests.AlertWorker;

public class RetryCountHeaderTests
{
    [Fact]
    public void Missing_properties_means_first_attempt()
    {
        Assert.Equal(0, StockPriceConsumer.GetRetryCount(null));
    }

    [Fact]
    public void Missing_headers_means_first_attempt()
    {
        var properties = new BasicProperties();

        Assert.Equal(0, StockPriceConsumer.GetRetryCount(properties));
    }

    [Fact]
    public void Headers_without_retry_count_mean_first_attempt()
    {
        var properties = WithHeader("some-other-header", Encoding.UTF8.GetBytes("5"));

        Assert.Equal(0, StockPriceConsumer.GetRetryCount(properties));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    public void Retry_count_is_parsed_from_utf8_bytes(string headerValue, int expected)
    {
        // RabbitMQ delivers string header values as byte[], which is also how
        // the consumer writes x-retry-count when republishing to the retry queue.
        var properties = WithHeader("x-retry-count", Encoding.UTF8.GetBytes(headerValue));

        Assert.Equal(expected, StockPriceConsumer.GetRetryCount(properties));
    }

    [Fact]
    public void Non_numeric_retry_count_is_treated_as_first_attempt()
    {
        var properties = WithHeader("x-retry-count", Encoding.UTF8.GetBytes("abc"));

        Assert.Equal(0, StockPriceConsumer.GetRetryCount(properties));
    }

    [Fact]
    public void Integer_typed_retry_count_is_ignored()
    {
        // Only byte[] values are understood. A producer that sets the header as
        // an AMQP integer would have it treated as a first attempt.
        var properties = WithHeader("x-retry-count", 2);

        Assert.Equal(0, StockPriceConsumer.GetRetryCount(properties));
    }

    private static BasicProperties WithHeader(string key, object value) =>
        new()
        {
            Headers = new Dictionary<string, object?> { [key] = value }
        };
}
