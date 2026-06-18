using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;

namespace EasyRabbitFlow.Tests.Helpers;

public class TestEvent
{
    public string Id { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class TestConsumer : IRabbitFlowConsumer<TestEvent>
{
    private static readonly ConcurrentBag<TestEvent> _received = new();

    public static IReadOnlyList<TestEvent> ReceivedMessages => _received.ToList();

    public Task HandleAsync(TestEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        _received.Add(message);
        return Task.CompletedTask;
    }

    public static void Reset()
    {
        _received.Clear();
    }
}

public record CapturedTrace(TestEvent Message, string? ActivityTraceId, string? TraceParentHeader);

public class TraceCapturingConsumer : IRabbitFlowConsumer<TestEvent>
{
    private static readonly ConcurrentBag<CapturedTrace> _received = new();

    public static IReadOnlyList<CapturedTrace> Received => _received.ToList();

    public Task HandleAsync(TestEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        string? traceparent = null;

        if (context.Headers != null && context.Headers.TryGetValue("traceparent", out var raw))
        {
            traceparent = raw switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string text => text,
                _ => raw?.ToString()
            };
        }

        _received.Add(new CapturedTrace(message, Activity.Current?.TraceId.ToString(), traceparent));

        return Task.CompletedTask;
    }

    public static void Reset()
    {
        _received.Clear();
    }
}

public class PoisonSerializationEvent
{
    public string Id { get; set; } = string.Empty;

    public bool ShouldThrow { get; set; }

    // Throws during JSON serialization to force a publish-stage failure.
    public string Payload => ShouldThrow
        ? throw new InvalidOperationException("PoisonSerializationEvent: intentional serialization failure")
        : "ok";
}

public class AlwaysFailConsumer : IRabbitFlowConsumer<TestEvent>
{
    private static readonly ConcurrentBag<TestEvent> _received = new();

    public static IReadOnlyList<TestEvent> ReceivedMessages => _received.ToList();

    public Task HandleAsync(TestEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        _received.Add(message);
        throw new InvalidOperationException("AlwaysFailConsumer: intentional failure");
    }

    public static void Reset()
    {
        _received.Clear();
    }
}

public class DerivedTransientException : EasyRabbitFlow.Exceptions.RabbitFlowTransientException
{
    public DerivedTransientException(string message) : base(message) { }
}

public class AlwaysDerivedTransientFailConsumer : IRabbitFlowConsumer<TestEvent>
{
    public Task HandleAsync(TestEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        throw new DerivedTransientException("AlwaysDerivedTransientFailConsumer: intentional transient failure");
    }
}

public class TransientFailConsumer : IRabbitFlowConsumer<TestEvent>
{
    private static int _callCount;
    private static readonly ConcurrentBag<TestEvent> _received = new();

    public static IReadOnlyList<TestEvent> ReceivedMessages => _received.ToList();

    public Task HandleAsync(TestEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        var count = Interlocked.Increment(ref _callCount);

        // Fail on first attempt, succeed on subsequent
        if (count == 1)
        {
            throw new EasyRabbitFlow.Exceptions.RabbitFlowTransientException("Transient failure");
        }

        _received.Add(message);
        return Task.CompletedTask;
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _callCount, 0);
        _received.Clear();
    }
}
