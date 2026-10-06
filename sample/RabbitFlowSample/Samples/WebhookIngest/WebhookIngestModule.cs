using Microsoft.AspNetCore.Mvc;

namespace RabbitFlowSample.Samples.WebhookIngest;

// Webhook ingestion over an infinite IAsyncEnumerable<T> source with backpressure.
//
// Events arrive whenever the external provider decides to send them: a burst of hundreds after an
// outage, then hours of silence. The HTTP endpoint only accepts the event into a bounded in-process
// buffer and answers 202; a hosted service keeps a single long-lived temporary run reading from that
// buffer. See WebhookIngestPipeline for the backpressure chain and README.md for the guarantees.
public static class WebhookIngestModule
{
    public static void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<WebhookIngestPipeline>();
        services.AddHostedService(sp => sp.GetRequiredService<WebhookIngestPipeline>());
    }

    public static void MapEndpoints(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/webhooks").WithTags("Webhook Ingest");

        // What a real provider would call. Accepting means "buffered", not "processed".
        group.MapPost("/{provider}", async (
            string provider,
            [FromBody] WebhookRequest request,
            WebhookIngestPipeline pipeline,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.EventId) || string.IsNullOrWhiteSpace(request.Type))
            {
                return Results.BadRequest(new { error = "eventId and type are required." });
            }

            var evt = new WebhookEvent(provider, request.EventId, request.Type, DateTime.UtcNow, request.Payload);

            if (await pipeline.TryAcceptAsync(evt, ct))
            {
                return Results.Accepted(value: new { accepted = true, buffered = pipeline.GetStatus().Buffered });
            }

            // Backpressure reached the sender: the buffer stayed full. Providers retry on 429/5xx.
            return Results.Json(new { accepted = false, error = "Ingest buffer is full; retry later." },
                statusCode: StatusCodes.Status429TooManyRequests);
        })
        .WithName("ReceiveWebhook")
        .WithSummary("Accepts one webhook event into the bounded ingest buffer (202) or rejects it when the buffer is full (429).")
        .Produces(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status429TooManyRequests);

        // Test helper: simulates a provider replaying a backlog with `concurrency` parallel senders, the way
        // real providers do. Handlers drain at ~16 events/s (4 x 250 ms), so the 16-message in-flight window
        // fills first, then the 64-slot buffer, and senders that wait more than 500 ms for a slot get 429.
        group.MapPost("/{provider}/burst", async (
            string provider,
            WebhookIngestPipeline pipeline,
            CancellationToken ct,
            [FromQuery] int count = 200,
            [FromQuery] int concurrency = 32,
            [FromQuery] int failEvery = 0) =>
        {
            if (count is < 1 or > 2000 || concurrency is < 1 or > 256 || failEvery < 0)
            {
                return Results.BadRequest(new { error = "Use 1–2000 events, 1–256 concurrent senders and a non-negative failEvery." });
            }

            var accepted = 0;
            var rejected = 0;
            using var senders = new SemaphoreSlim(concurrency, concurrency);

            await Task.WhenAll(Enumerable.Range(1, count).Select(async i =>
            {
                await senders.WaitAsync(ct);
                try
                {
                    var type = failEvery > 0 && i % failEvery == 0 ? "order.updated.fail" : "order.updated";
                    var evt = new WebhookEvent(provider, $"{provider}-{Guid.NewGuid():N}", type, DateTime.UtcNow, null);

                    if (await pipeline.TryAcceptAsync(evt, ct)) Interlocked.Increment(ref accepted);
                    else Interlocked.Increment(ref rejected);
                }
                finally
                {
                    senders.Release();
                }
            }));

            return Results.Ok(new { requested = count, concurrency, accepted, rejected, status = pipeline.GetStatus() });
        })
        .WithName("BurstWebhooks")
        .WithSummary("Simulates a provider replaying a backlog: shows the buffer filling and 429 rejections under backpressure.")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);

        group.MapGet("/status", (WebhookIngestPipeline pipeline) => Results.Ok(pipeline.GetStatus()))
        .WithName("WebhookIngestStatus")
        .WithSummary("Counters of the ingest pipeline: accepted/rejected, buffered, processed/failed, current and last run.")
        .Produces<WebhookIngestStatus>(StatusCodes.Status200OK);
    }
}
