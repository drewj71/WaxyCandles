using System.Diagnostics;
using System.Text;
using RabbitMQ.Client;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Telemetry;

namespace WaxyCandles.Tests.Telemetry;

/// <summary>
/// Run on their own: a <see cref="SpanRecorder"/> is a process-wide listener,
/// which would turn tracing on for tests running alongside.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TracingCollection
{
    public const string Name = "Tracing";
}

/// <summary>Trace context through an outbox row and through message headers.</summary>
[Collection(TracingCollection.Name)]
public class MessageTracingTests
{
    private const string TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    [Fact]
    public void Outbox_row_round_trips_the_current_trace_context()
    {
        using var recorder = new SpanRecorder();
        using var span = WaxyCandlesTelemetry.Source.StartActivity("write row")!;
        span.TraceStateString = "vendor=value";

        var row = NewRow();
        MessageTracing.Stamp(row);

        Assert.Equal(span.Id, row.TraceParent);
        Assert.Equal(55, row.TraceParent!.Length);
        Assert.Equal("vendor=value", row.TraceState);

        var context = MessageTracing.FromOutbox(row);
        Assert.Equal(span.TraceId, context.TraceId);
        Assert.Equal(span.SpanId, context.SpanId);
        Assert.True(context.IsRemote);
    }

    [Theory]
    [InlineData(512, true)]
    [InlineData(513, false)]
    public void Tracestate_too_long_for_its_column_is_dropped_not_truncated(int length, bool kept)
    {
        using var recorder = new SpanRecorder();
        using var span = WaxyCandlesTelemetry.Source.StartActivity("write row")!;
        span.TraceStateString = "k=" + new string('v', length - 2);

        var row = NewRow();
        MessageTracing.Stamp(row);

        // The traceparent alone still links the trace.
        Assert.Equal(span.Id, row.TraceParent);
        Assert.Equal(kept ? span.TraceStateString : null, row.TraceState);
    }

    [Fact]
    public void No_current_span_leaves_the_row_without_context()
    {
        Assert.Null(Activity.Current);

        var row = NewRow();
        MessageTracing.Stamp(row);

        Assert.Null(row.TraceParent);
        Assert.Equal(default, MessageTracing.FromOutbox(row));
    }

    [Theory]
    [InlineData("not-a-traceparent")]
    [InlineData("00-00000000000000000000000000000000-b7ad6b7169203331-01")]
    [InlineData("")]
    public void Malformed_context_starts_a_new_trace(string traceParent)
    {
        var row = NewRow();
        row.TraceParent = traceParent;

        Assert.Equal(default, MessageTracing.FromOutbox(row));
    }

    [Fact]
    public void Headers_round_trip_the_current_trace_context_as_bytes()
    {
        using var recorder = new SpanRecorder();
        using var span = WaxyCandlesTelemetry.Source.StartActivity("publish")!;

        var headers = new Dictionary<string, object?>();
        MessageTracing.InjectCurrent(headers);

        // RabbitMQ sends string headers as byte[], which is what consumers get back.
        Assert.IsType<byte[]>(headers[MessageTracing.TraceParentHeader]);

        var context = MessageTracing.FromHeaders(new BasicProperties { Headers = headers });
        Assert.Equal(span.TraceId, context.TraceId);
        Assert.Equal(span.SpanId, context.SpanId);
    }

    [Fact]
    public void String_header_values_are_read_too()
    {
        var context = MessageTracing.FromHeaders(new BasicProperties
        {
            Headers = new Dictionary<string, object?> { [MessageTracing.TraceParentHeader] = TraceParent }
        });

        Assert.Equal("0af7651916cd43dd8448eb211c80319c", context.TraceId.ToHexString());
    }

    [Fact]
    public void Missing_headers_start_a_new_trace()
    {
        Assert.Equal(default, MessageTracing.FromHeaders(null));
        Assert.Equal(default, MessageTracing.FromHeaders(new BasicProperties()));
        Assert.Equal(default, MessageTracing.FromHeaders(new BasicProperties
        {
            Headers = new Dictionary<string, object?> { ["x-retry-count"] = Encoding.UTF8.GetBytes("1") }
        }));
    }

    [Fact]
    public void Retry_and_dead_letter_republishes_copy_the_original_trace_headers()
    {
        var traceParent = Encoding.UTF8.GetBytes(TraceParent);
        var original = new BasicProperties
        {
            Headers = new Dictionary<string, object?>
            {
                [MessageTracing.TraceParentHeader] = traceParent,
                ["x-retry-count"] = Encoding.UTF8.GetBytes("1")
            }
        };

        var copy = new Dictionary<string, object?>();
        MessageTracing.CopyHeaders(original, copy);

        Assert.Same(traceParent, copy[MessageTracing.TraceParentHeader]);
        // Only trace headers; the retry count is the caller's to set.
        Assert.Single(copy);
    }

    private static OutboxMessage NewRow() => new()
    {
        Id = Guid.NewGuid(),
        Type = "Test",
        Payload = "{}",
        CreatedAt = DateTimeOffset.UtcNow
    };
}
