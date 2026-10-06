using System.Text.Json;

namespace RabbitFlowSample.Samples.WebhookIngest;

// One inbound webhook call, as accepted by the HTTP endpoint. Arrival is unpredictable: bursts of
// hundreds in a second, then nothing for hours. The pipeline never knows how many will arrive.
public sealed record WebhookEvent(
    string Provider,
    string EventId,
    string Type,
    DateTime ReceivedUtc,
    JsonElement? Payload);

// Body of POST /webhooks/{provider}. Only EventId and Type are required; Payload is opaque.
public sealed record WebhookRequest(string EventId, string Type, JsonElement? Payload);

public sealed record WebhookIngestStatus(
    long Accepted,
    long Rejected,
    int Buffered,
    int BufferCapacity,
    int MaxInFlightMessages,
    long Processed,
    long Failed,
    long Runs,
    DateTime? CurrentRunStartedUtc,
    WebhookRunSummary? LastCompletedRun);

public sealed record WebhookRunSummary(
    string? QueueName,
    DateTime StartedUtc,
    DateTime CompletedUtc,
    bool? SourceCompleted,
    int TotalMessages,
    int SucceededMessages,
    int FailedMessages,
    int ErrorCount,
    string? EndReason);
