using System.Text.Json.Serialization;

namespace RabbitFlowSample.Samples.Orders;

// Payload published to the "orders-topic" exchange with routing keys shaped
// "orders.{region}.{status}" — e.g. "orders.eu.created", "orders.us.shipped".
// Each subscriber binds to a different pattern (see the three OrderEvent consumers).
public sealed class OrderEvent
{
    [JsonPropertyName("orderId")]
    public Guid OrderId { get; set; } = Guid.NewGuid();

    [JsonPropertyName("region")]
    public string Region { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("customerEmail")]
    public string? CustomerEmail { get; set; }

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }
}
