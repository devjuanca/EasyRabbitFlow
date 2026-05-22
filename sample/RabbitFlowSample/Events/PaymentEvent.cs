using System.Text.Json.Serialization;

namespace RabbitFlowSample.Events;

// Sample payload used to demonstrate DeadLetterFanouts.
// Set ShouldFail = true or Amount <= 0 to force PaymentConsumer to throw,
// so the message is dead-lettered and fanned out to:
//   - payments-queue-deadletter  (primary DLQ)
//   - payments-audit             (long-retention copy)
//   - payments-alerts            (consumed live by PaymentAlertsConsumer)
public class PaymentEvent
{
    [JsonPropertyName("paymentId")]
    public Guid PaymentId { get; set; } = Guid.NewGuid();

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "EUR";

    [JsonPropertyName("shouldFail")]
    public bool ShouldFail { get; set; }
}
