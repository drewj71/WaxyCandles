using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace WaxyCandles.Tests.Infrastructure;

/// <summary>
/// Records formatted log messages so tests can wait on consumer events
/// (e.g. "already processed, skipping") that leave no other observable trace.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => _messages.ToArray();

    public bool Contains(string fragment) =>
        _messages.Any(m => m.Contains(fragment, StringComparison.Ordinal));

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            messages.Enqueue(formatter(state, exception));
    }
}
