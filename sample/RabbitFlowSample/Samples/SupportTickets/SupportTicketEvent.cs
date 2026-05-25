using System.Text.Json.Serialization;

namespace RabbitFlowSample.Samples.SupportTickets;

// Payload published to the priority queue "support-tickets-queue".
// The Severity is mapped to an AMQP priority (0..9) when the publisher sets
// PublishOptions.Priority; the broker then delivers higher-priority tickets first
// while a backlog exists.
public sealed class SupportTicketEvent
{
    [JsonPropertyName("ticketId")]
    public Guid TicketId { get; set; } = Guid.NewGuid();

    [JsonPropertyName("customerEmail")]
    public string CustomerEmail { get; set; } = string.Empty;

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public TicketSeverity Severity { get; set; } = TicketSeverity.Normal;
}

// Logical severity exposed to API callers. The mapping to AMQP priority lives in
// SupportTicketsModule.MapPriority so the wire format never leaks into the payload.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TicketSeverity
{
    Low,
    Normal,
    High,
    Critical
}

// Shape of POST /support/tickets/burst — caller controls how many tickets of each
// severity are pushed and in which order they hit the queue. Submitting them with
// the "lowest first" order is the most dramatic demo: the broker still drains
// criticals first.
public sealed class TicketBurstRequest
{
    [JsonPropertyName("lowCount")]
    public int LowCount { get; set; }

    [JsonPropertyName("normalCount")]
    public int NormalCount { get; set; }

    [JsonPropertyName("highCount")]
    public int HighCount { get; set; }

    [JsonPropertyName("criticalCount")]
    public int CriticalCount { get; set; }
}
