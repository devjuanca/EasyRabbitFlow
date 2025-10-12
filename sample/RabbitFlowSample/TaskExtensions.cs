namespace RabbitFlowSample;

public static class TaskExtensions
{
    public static void FireAndForget(this Task task, Action<Exception>? errorHandler = null, CancellationToken cancellationToken = default)
    {
        task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is not null && errorHandler != null)
                errorHandler(t.Exception);

        }, cancellationToken, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }
}

