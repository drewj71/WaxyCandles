using System.Collections.Concurrent;
using System.Diagnostics;
using WaxyCandles.Infrastructure.Telemetry;

namespace WaxyCandles.Tests.Telemetry;

/// <summary>
/// Records every finished span from the app's ActivitySource, standing in for
/// the OpenTelemetry SDK. While one exists, spans are created (with no
/// listener, StartActivity returns null).
/// </summary>
public sealed class SpanRecorder : IDisposable
{
    private readonly ConcurrentQueue<Activity> _spans = new();
    private readonly ActivityListener _listener;

    public SpanRecorder()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == WaxyCandlesTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _spans.Enqueue(activity)
        };

        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyList<Activity> Spans => _spans.ToArray();

    public IReadOnlyList<Activity> InTrace(ActivityTraceId traceId) =>
        Spans.Where(x => x.TraceId == traceId).ToList();

    public void Dispose() => _listener.Dispose();
}
