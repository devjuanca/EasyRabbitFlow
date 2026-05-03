using System.Text.Json.Serialization;

namespace RabbitFlowSample.Events;

// Sample payload published to a Topic exchange ("orders-topic") with routing keys
// shaped like "orders.{region}.{status}" — e.g. "orders.eu.created", "orders.us.shipped".
// Each subscriber binds to a different pattern (see the OrderEvent consumers).
public class OrderEvent
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
