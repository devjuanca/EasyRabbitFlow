using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using EasyRabbitFlow.Settings;

namespace RabbitFlowSample.Samples.RunMonitoring;

// Where OnProgress writes and the status endpoint reads. An in-memory dictionary is enough to try the feature
// in one process; with several replicas this would be a shared store (Redis, a database) keyed the same way.
public sealed class RunProgressStore
{
    // Progress arrives every ProgressInterval even when nothing changes, so a report older than a few intervals
    // means the run, or the process running it, is gone.
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan StaleAfter = ProgressInterval * 3;

    private readonly ConcurrentDictionary<string, RunStatus> _runs = new();

    public void Started(string runId, int totalMessages) =>
        _runs[runId] = new RunStatus { RunId = runId, State = RunState.Starting, TotalMessages = totalMessages };

    public void Report(TemporaryRunProgress progress) =>
        _runs.AddOrUpdate(progress.CorrelationId!, _ => FromProgress(progress), (_, current) =>
            // Completion is final: a progress report never takes a finished run back to Running.
            current.State is RunState.Completed or RunState.FailedToStart ? current : FromProgress(progress));

    public void Completed(TemporaryRunResult result) =>
        _runs[result.CorrelationId!] = new RunStatus
        {
            RunId = result.CorrelationId!,
            State = RunState.Completed,
            QueueName = result.QueueName,
            TotalMessages = result.TotalMessages,
            PublishedMessages = result.PublishedMessages,
            ProcessedMessages = result.ProcessedMessages,
            SucceededMessages = result.SucceededMessages,
            FailedMessages = result.FailedMessages,
            LastReportUtc = result.CompletedUtc,
            Success = result.Success,
            Duration = result.Duration,
            Errors = result.Errors.Select(e => $"{e.Stage}: {e.Message}").Take(10).ToList()
        };

    public void FailedToStart(string runId, string error) =>
        _runs[runId] = new RunStatus { RunId = runId, State = RunState.FailedToStart, Errors = [error] };

    public bool TryGet(string runId, out RunStatus status)
    {
        if (!_runs.TryGetValue(runId, out status!))
        {
            return false;
        }

        if (status.State == RunState.Running && DateTime.UtcNow - status.LastReportUtc > StaleAfter)
        {
            status = status with { State = RunState.Stale };
        }

        return true;
    }

    public IReadOnlyList<RunStatus> All() =>
        _runs.Keys.Select(id => TryGet(id, out var status) ? status : null).OfType<RunStatus>().ToList();

    private static RunStatus FromProgress(TemporaryRunProgress progress) => new()
    {
        RunId = progress.CorrelationId!,
        State = RunState.Running,
        QueueName = progress.QueueName,
        TotalMessages = progress.TotalMessages,
        PublishedMessages = progress.PublishedMessages,
        ProcessedMessages = progress.ProcessedMessages,
        SucceededMessages = progress.SucceededMessages,
        FailedMessages = progress.FailedMessages,
        InFlightMessages = progress.InFlightMessages,
        LastReportUtc = progress.TimestampUtc
    };
}

[JsonConverter(typeof(JsonStringEnumConverter<RunState>))]
public enum RunState
{
    // Accepted; the first progress report has not arrived yet.
    Starting,
    Running,
    // Running, but no progress report for several intervals.
    Stale,
    Completed,
    // The run could not even start (e.g. the broker is unreachable).
    FailedToStart
}

public sealed record RunStatus
{
    public required string RunId { get; init; }
    public RunState State { get; init; }
    public string? QueueName { get; init; }
    public int TotalMessages { get; init; }
    public int PublishedMessages { get; init; }
    public int ProcessedMessages { get; init; }
    public int SucceededMessages { get; init; }
    public int FailedMessages { get; init; }
    public int InFlightMessages { get; init; }
    public DateTime? LastReportUtc { get; init; }
    public bool? Success { get; init; }
    public TimeSpan? Duration { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
}
