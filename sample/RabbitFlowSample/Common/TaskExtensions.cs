namespace RabbitFlowSample.Common;

public static class TaskExtensions
{
    // Detaches a Task so the caller does not have to await it, while still observing
    // any faulted exception through an optional handler. Used by the fire-and-forget
    // thumbnail endpoint to return 202 immediately while the temporary queue keeps draining.
    public static void FireAndForget(this Task task, Action<Exception>? errorHandler = null, CancellationToken cancellationToken = default)
    {
        task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is not null && errorHandler != null)
            {
                errorHandler(t.Exception);
            }
        }, cancellationToken, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }
}
