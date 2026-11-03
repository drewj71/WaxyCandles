using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using WaxyCandles.Infrastructure.Telemetry;

namespace WaxyCandles.Tests.Telemetry;

/// <summary>
/// Captures measurements from the app's meter with the built-in
/// <see cref="MeterListener"/>, as an OTLP exporter would receive them.
/// The meter is process-wide, so assert on measurements tagged for what the
/// test drives (its queue, endpoint, ...) and recorded after this was created.
/// </summary>
public sealed class MetricRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<Measurement> _measurements = new();
    private readonly object _observeLock = new();
    private List<Measurement>? _observing;

    public sealed record Measurement(
        string Instrument,
        double Value,
        IReadOnlyDictionary<string, object?> Tags)
    {
        public bool HasTags(params (string Key, object? Value)[] tags) =>
            tags.All(t => Tags.TryGetValue(t.Key, out var v) && Equals(v, t.Value));
    }

    public MetricRecorder()
    {
        // Runs WaxyCandlesMetrics' static constructor, which creates the gauges.
        _ = WaxyCandlesMetrics.Meter;

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == WaxyCandlesMetrics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>((i, v, t, _) => OnMeasurement(i, v, t));
        _listener.SetMeasurementEventCallback<double>((i, v, t, _) => OnMeasurement(i, v, t));
        _listener.SetMeasurementEventCallback<int>((i, v, t, _) => OnMeasurement(i, v, t));
        _listener.Start();
    }

    /// <summary>Counter and histogram measurements of one instrument with all the given tags.</summary>
    public IReadOnlyList<Measurement> Measurements(
        string instrument,
        params (string Key, object? Value)[] tags) =>
        _measurements
            .Where(m => m.Instrument == instrument && m.HasTags(tags))
            .ToList();

    /// <summary>The sum of a counter's measurements with all the given tags.</summary>
    public double Sum(string instrument, params (string Key, object? Value)[] tags) =>
        Measurements(instrument, tags).Sum(m => m.Value);

    /// <summary>Runs the observable instruments' callbacks now and returns one gauge's values.</summary>
    public IReadOnlyList<Measurement> Observe(
        string instrument,
        params (string Key, object? Value)[] tags)
    {
        lock (_observeLock)
        {
            _observing = [];

            try
            {
                _listener.RecordObservableInstruments();

                return _observing
                    .Where(m => m.Instrument == instrument && m.HasTags(tags))
                    .ToList();
            }
            finally
            {
                _observing = null;
            }
        }
    }

    /// <summary>The single current value of a gauge, or null if it reports none.</summary>
    public double? Gauge(string instrument, params (string Key, object? Value)[] tags) =>
        Observe(instrument, tags) is [var only] ? only.Value : null;

    public void Dispose() => _listener.Dispose();

    private void OnMeasurement<T>(
        Instrument instrument,
        T value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct
    {
        var measurement = new Measurement(
            instrument.Name,
            Convert.ToDouble(value),
            tags.ToArray().ToDictionary(t => t.Key, t => t.Value));

        if (instrument.IsObservable)
        {
            // Only during Observe, which holds the lock on this thread.
            _observing?.Add(measurement);
            return;
        }

        _measurements.Enqueue(measurement);
    }
}

/// <summary>
/// For tests that assert exact metric values from code other tests also run
/// outside the integration collection (e.g. the Market Data client): run them
/// on their own, after the parallel collections.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MetricsCollection
{
    public const string Name = "Metrics";
}
