using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using Microsoft.AspNetCore.Mvc;
using RabbitMQ.Client.Exceptions;

namespace RabbitFlowSample.Samples.RunMonitoring;

// Observes a fire-and-forget temporary run from a separate endpoint. The run reports itself through
// RunTemporaryOptions.OnProgress into RunProgressStore; the status endpoint only reads the store, so it does not
// need the run's Task, its process or a connection to its (exclusive) queue.
public static class RunMonitoringModule
{
    public static void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<RunProgressStore>();
    }

    public static void MapEndpoints(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/run-monitoring/runs").WithTags("Run Monitoring");

        group.MapPost("", (
            [FromBody] StartRunRequest request,
            IRabbitFlowTemporary temporary,
            RunProgressStore store,
            IHostApplicationLifetime lifetime,
            ILogger<Program> logger) =>
        {
            if (request.Messages is < 1 or > 500 || request.HandlerDelayMs is < 0 or > 5000 ||
                request.FailEvery is < 0 or > 500 || request.PrefetchCount is < 1 or > 16)
            {
                return Results.BadRequest(new { error = "Use 1–500 messages, a handler delay of 0–5000 ms, failEvery 0–500 (0 = never) and a prefetch of 1–16." });
            }

            // The id the caller polls with. It is the run's CorrelationId, so every progress snapshot carries it.
            var runId = $"run-{Guid.NewGuid():N}";

            store.Started(runId, request.Messages);

            var jobs = Enumerable.Range(1, request.Messages).Select(number => new MonitoredJob(number)).ToList();

            // Fire-and-forget: the response returns at once. The run is tied to the application's lifetime, not to
            // the HTTP request, so it keeps going after the response is sent.
            _ = Task.Run(async () =>
            {
                try
                {
                    await temporary.RunAsync(
                        jobs,
                        async (job, ct) =>
                        {
                            await Task.Delay(request.HandlerDelayMs, ct);

                            if (request.FailEvery > 0 && job.Number % request.FailEvery == 0)
                            {
                                throw new InvalidOperationException($"Job {job.Number} failed on purpose.");
                            }
                        },
                        onCompletedAsync: (result, _) =>
                        {
                            store.Completed(result);
                            logger.LogInformation("[RunMonitoring] {RunId} completed. Succeeded={Succeeded}, Failed={Failed}, Duration={Duration}",
                                runId, result.SucceededMessages, result.FailedMessages, result.Duration);
                            return Task.CompletedTask;
                        },
                        options: new RunTemporaryOptions
                        {
                            CorrelationId = runId,
                            QueuePrefixName = "run-monitoring",
                            PrefetchCount = request.PrefetchCount,
                            ProgressInterval = RunProgressStore.ProgressInterval,
                            OnProgress = (progress, _) =>
                            {
                                store.Report(progress);
                                return Task.CompletedTask;
                            }
                        },
                        cancellationToken: lifetime.ApplicationStopping);
                }
                catch (Exception ex)
                {
                    // RunAsync only throws when the run cannot start (e.g. broker unreachable); nothing was processed.
                    store.FailedToStart(runId, ex.Message);
                    logger.LogError(ex, "[RunMonitoring] {RunId} could not start", runId);
                }
            });

            return Results.Accepted($"/run-monitoring/runs/{runId}", new { runId });
        })
        .WithName("StartMonitoredRun")
        .WithSummary("Starts a fire-and-forget temporary run that reports its progress to a store.")
        .Produces(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status400BadRequest);

        group.MapGet("/{runId}", (string runId, RunProgressStore store) =>
            store.TryGet(runId, out var status) ? Results.Ok(status) : Results.NotFound())
        .WithName("GetMonitoredRun")
        .WithSummary("Returns the last stored progress of a run, or its final result once completed.")
        .Produces<RunStatus>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        group.MapGet("", (RunProgressStore store) => Results.Ok(store.All()))
        .WithName("ListMonitoredRuns")
        .WithSummary("Lists every run known to this process.")
        .Produces<IReadOnlyList<RunStatus>>(StatusCodes.Status200OK);

        // Why the store is needed: the broker refuses to inspect a temporary (exclusive) queue from any other
        // connection, and IRabbitFlowState always opens its own.
        group.MapGet("/{runId}/broker-state", async (string runId, RunProgressStore store, IRabbitFlowState state, CancellationToken ct) =>
        {
            if (!store.TryGet(runId, out var status) || status.QueueName is null)
            {
                return Results.NotFound(new { error = "Unknown run, or no progress reported yet (the queue name arrives with the first report)." });
            }

            try
            {
                return Results.Ok(await state.GetQueueStateAsync(status.QueueName, ct));
            }
            catch (OperationInterruptedException ex)
            {
                return Results.Ok(new { status.QueueName, brokerReplyCode = ex.ShutdownReason?.ReplyCode, brokerReply = ex.ShutdownReason?.ReplyText });
            }
        })
        .WithName("GetMonitoredRunBrokerState")
        .WithSummary("Tries IRabbitFlowState on the run's temporary queue, to show why the progress store is needed.");
    }
}

public sealed record StartRunRequest(int Messages = 40, int HandlerDelayMs = 500, int FailEvery = 7, ushort PrefetchCount = 2);

public sealed record MonitoredJob(int Number);
