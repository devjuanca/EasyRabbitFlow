using System.Collections.Concurrent;
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
