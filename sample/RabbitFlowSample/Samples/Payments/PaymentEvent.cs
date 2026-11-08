using System.Text.Json.Serialization;

namespace RabbitFlowSample.Samples.Payments;

// Payload used to demonstrate DeadLetterReplicas.
// Set ShouldFail = true or Amount <= 0 to force PaymentConsumer to throw, so the message
// is dead-lettered and replicated to:
//   - payments-queue-deadletter  (primary DLQ, drained by the reprocessor)
//   - payments-audit             (long-retention replica, inspected manually)
//   - payments-alerts            (consumed live by PaymentAlertsConsumer)
public sealed class PaymentEvent
{
    [JsonPropertyName("paymentId")]
    public Guid PaymentId { get; set; } = Guid.NewGuid();

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "EUR";

    [JsonPropertyName("shouldFail")]
    public bool ShouldFail { get; set; }

    // How long the handler holds the delivery (unacknowledged) before completing. Lets you drop the
    // connection while a payment is in flight to watch the broker return it (x-delivery-count) and,
    // after MaxBrokerDeliveries returns, the poison guard dead-letter it without running the handler.
    [JsonPropertyName("processingDelayMs")]
    public int ProcessingDelayMs { get; set; } = 200;
}
