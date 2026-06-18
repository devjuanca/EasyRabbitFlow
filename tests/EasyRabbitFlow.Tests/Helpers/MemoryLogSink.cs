using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace EasyRabbitFlow.Tests.Helpers;

// Test-only logger that captures log lines into a static, thread-safe buffer so a test can assert on
// (or dump) what the library logged. Useful for diagnosing recovery/topology behavior under Testcontainers.
public static class MemoryLogSink
{
    public static readonly ConcurrentQueue<string> Lines = new();

    public static void Reset() => Lines.Clear();

    public static string Dump() => string.Join(Environment.NewLine, Lines);
}

public sealed class MemoryLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new MemoryLogger();

    public void Dispose() { }

    private sealed class MemoryLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            MemoryLogSink.Lines.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {logLevel}: {message}{(exception != null ? " | " + exception.GetType().Name + ": " + exception.Message : string.Empty)}");
        }
    }
}
