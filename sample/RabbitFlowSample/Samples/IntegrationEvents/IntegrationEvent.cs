using System.Text.Json.Serialization;

namespace RabbitFlowSample.Samples.IntegrationEvents;

// Payload published to the "integration-events" exchange with routing keys shaped
// "integration.{domain}.{action}" — e.g. "integration.user.created", "integration.booking.cancelled".
// This service owns the exchange but knows nothing about who subscribes: external clients bind
// their own queues with whatever patterns they need (see IntegrationEventsModule).
public sealed class IntegrationEvent
{
    [JsonPropertyName("eventId")]
    public Guid EventId { get; set; } = Guid.NewGuid();

    [JsonPropertyName("domain")]
    public string Domain { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("occurredAtUtc")]
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}
