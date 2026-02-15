using EasyRabbitFlow.Services;

namespace EasyRabbitFlow.Tests.Helpers;

public class TestEvent
{
    public string Id { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class TestConsumer : IRabbitFlowConsumer<TestEvent>
{
    public static readonly List<TestEvent> ReceivedMessages = new();
    private static readonly object _lock = new();

    public Task HandleAsync(TestEvent message, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ReceivedMessages.Add(message);
        }
        return Task.CompletedTask;
    }

    public static void Reset() 
    { 
        lock (_lock) 
        { 
            ReceivedMessages.Clear(); 
        } 
    }
}

public class TransientFailConsumer : IRabbitFlowConsumer<TestEvent>
{
    private static int _callCount;
    public static readonly List<TestEvent> ReceivedMessages = new();
    private static readonly object _lock = new();

    public Task HandleAsync(TestEvent message, CancellationToken cancellationToken)
    {
        var count = Interlocked.Increment(ref _callCount);

        // Fail on first attempt, succeed on subsequent
        if (count == 1)
        {
            throw new EasyRabbitFlow.Exceptions.RabbitFlowTransientException("Transient failure");
        }

        lock (_lock)
        {
            ReceivedMessages.Add(message);
        }
        return Task.CompletedTask;
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _callCount, 0);
        lock (_lock) 
        { 
            ReceivedMessages.Clear(); 
        }
    }
}
