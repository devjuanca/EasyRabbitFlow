using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace EasyRabbitFlow.Tests.Helpers;

// Test-only logger that captures log lines into a static, thread-safe buffer so a test can assert on
// (or dump) what the library logged. Useful for diagnosing recovery/topology behavior under Testcontainers.
// Active log scopes are appended to every line as "key=value" pairs, so tests can assert that handler
// logs inherit the consumer's message scope.
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
        private static readonly AsyncLocal<ImmutableScopeStack?> _scopes = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            var parent = _scopes.Value;
            _scopes.Value = new ImmutableScopeStack(state, parent);
            return new ScopePop(parent);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var scope = FormatScopes(_scopes.Value);
            MemoryLogSink.Lines.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {logLevel}: {message}{(exception != null ? " | " + exception.GetType().Name + ": " + exception.Message : string.Empty)}{scope}");
        }

        private static string FormatScopes(ImmutableScopeStack? top)
        {
            if (top == null) return string.Empty;

            var parts = new List<string>();
            for (var s = top; s != null; s = s.Parent)
            {
                if (s.State is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    foreach (var kv in pairs) parts.Add($"{kv.Key}={kv.Value}");
                }
                else
                {
                    parts.Add(s.State?.ToString() ?? string.Empty);
                }
            }

            return parts.Count == 0 ? string.Empty : " {" + string.Join(", ", parts) + "}";
        }

        private sealed class ImmutableScopeStack
        {
            public ImmutableScopeStack(object state, ImmutableScopeStack? parent) { State = state; Parent = parent; }
            public object State { get; }
            public ImmutableScopeStack? Parent { get; }
        }

        private sealed class ScopePop : IDisposable
        {
            private readonly ImmutableScopeStack? _parent;
            public ScopePop(ImmutableScopeStack? parent) { _parent = parent; }
            public void Dispose() => _scopes.Value = _parent;
        }
    }
}
