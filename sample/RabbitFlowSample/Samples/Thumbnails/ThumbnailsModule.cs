using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using Microsoft.AspNetCore.Mvc;
using RabbitFlowSample.Common;
using System.Collections.Concurrent;

namespace RabbitFlowSample.Samples.Thumbnails;

// Temporary queue sample: fan a one-shot batch of work units out to ad-hoc workers
// through a queue that is created on demand, drained, and torn down — without any
// pre-declared topology or hosted consumer.
//
// Realistic use case: a request arrives with a list of image URLs that need
// thumbnails generated in parallel. Each image is "downloaded and resized" by a
// worker handler running off the temporary queue; the request returns when the
// whole batch finishes (await endpoint) or immediately while the batch keeps
// draining in the background (fire-and-forget endpoint).
public static class ThumbnailsModule
{
    // No consumer registration: IRabbitFlowTemporary creates and tears down the
    // queue per request. Only resolved via DI inside the endpoints.
    public static void RegisterConsumers(RabbitFlowConfigurator settings)
    {
        // Intentionally empty — kept for symmetry with the other sample modules.
    }

    public static void MapEndpoints(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/thumbnails").WithTags("Thumbnails");

        // Await-completion: returns after every thumbnail finishes (or fails).
        // Uses the TResult overload so each worker contributes a structured result
        // that the endpoint can return in the HTTP response.
        group.MapPost("/", async (
            [FromServices] IRabbitFlowTemporary temporary,
            ILogger<Program> logger,
            [FromBody] ThumbnailJob[] jobs,
            CancellationToken ct) =>
        {
            if (jobs is null || jobs.Length == 0)
            {
                return Results.BadRequest(new { error = "Provide at least one thumbnail job." });
            }

            // The TResult overload returns the processed count and pipes each per-job result
            // into a ConcurrentQueue that onCompletedAsync receives. Capture the queue here
            // so the endpoint can include the items in the HTTP response.
            ConcurrentQueue<ThumbnailResult>? collected = null;

            var processed = await temporary.RunAsync<ThumbnailJob, ThumbnailResult>(
                messages: jobs,
                onMessageReceived: async (job, workerCt) =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    // Simulate variable I/O: fetch the source image and resize it.
                    var latency = Random.Shared.Next(150, 600);

                    await Task.Delay(latency, workerCt);

                    var outputPath = $"/tmp/thumbnails/{Guid.NewGuid():N}_{job.Width}x{job.Height}.jpg";

                    logger.LogInformation(
                        "[Thumbnail] Resized {Url} → {Output} in {Ms}ms",
                        job.ImageUrl, outputPath, sw.ElapsedMilliseconds);

                    return new ThumbnailResult
                    {
                        ImageUrl = job.ImageUrl,
                        Width = job.Width,
                        Height = job.Height,
                        DurationMs = sw.ElapsedMilliseconds,
                        OutputPath = outputPath
                    };
                },
                onCompletedAsync: (total, queue) =>
                {
                    collected = queue;
                    logger.LogInformation("[Thumbnail] Batch finished. Total={Total}", total);
                    return Task.CompletedTask;
                },
                onError: (job, errorCt) =>
                {
                    logger.LogWarning("[Thumbnail] Failed to resize {Url}", job.ImageUrl);
                    return Task.CompletedTask;
                },
                options: new RunTemporaryOptions
                {
                    PrefetchCount = 4,
                    Timeout = TimeSpan.FromSeconds(10),
                    QueuePrefixName = "thumbnails",
                },
                cancellationToken: ct);

            return Results.Ok(new
            {
                requested = jobs.Length,
                processed,
                items = collected?.ToArray() ?? Array.Empty<ThumbnailResult>()
            });
        })
        .WithName("RunThumbnailBatch")
        .WithSummary("Generates thumbnails for the supplied images via a temporary queue and returns each result once the batch finishes.")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);

        // Fire-and-forget: returns 202 immediately; the temporary queue keeps draining
        // in the background. Useful when the caller does not need per-job results
        // (e.g. an upload pipeline that already persists output paths elsewhere).
        group.MapPost("/fire-and-forget", (
            [FromServices] IRabbitFlowTemporary temporary,
            ILogger<Program> logger,
            [FromBody] ThumbnailJob[] jobs) =>
        {
            if (jobs is null || jobs.Length == 0)
            {
                return Results.BadRequest(new { error = "Provide at least one thumbnail job." });
            }

            temporary.RunAsync(
                messages: jobs,
                onMessageReceived: async (job, workerCt) =>
                {
                    var latency = Random.Shared.Next(150, 600);

                    await Task.Delay(latency, workerCt);

                    logger.LogInformation(
                        "[Thumbnail-FaF] Resized {Url} ({W}x{H}) after {Ms}ms",
                        job.ImageUrl, job.Width, job.Height, latency);
                },
                onCompleted: (totalProcessed, errors) =>
                {
                    logger.LogInformation(
                        "[Thumbnail-FaF] Background batch finished. Total={Total}, Errors={Errors}",
                        totalProcessed, errors);
                },
                onError: (job, errorCt) =>
                {
                    logger.LogWarning("[Thumbnail-FaF] Failed to resize {Url}", job.ImageUrl);
                    return Task.CompletedTask;
                },
                options: new RunTemporaryOptions
                {
                    PrefetchCount = 4,
                    Timeout = TimeSpan.FromSeconds(10),
                    QueuePrefixName = "thumbnails",
                },
                cancellationToken: CancellationToken.None)
            .FireAndForget(errorHandler: ex =>
                logger.LogError(ex, "[Thumbnail-FaF] Background batch crashed"));

            return Results.Accepted(value: new { accepted = jobs.Length });
        })
        .WithName("RunThumbnailBatchFireAndForget")
        .WithSummary("Kicks off a thumbnail batch through a temporary queue and returns 202 immediately. Per-job results are logged only.")
        .Produces(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status400BadRequest);
    }
}
