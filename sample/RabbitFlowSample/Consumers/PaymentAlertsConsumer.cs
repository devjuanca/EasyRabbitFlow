using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;

namespace RabbitFlowSample.Consumers;

// Live alerting consumer attached to one of PaymentConsumer's dead-letter replicas.
// Receives the same DeadLetterEnvelope copy that landed on the primary DLQ, but
// independently: this consumer can fail, restart, or fall behind without affecting
// the primary DLQ flow or the reprocessor.
//
// Registered with AutoGenerate = false because the queue is declared by
// PaymentConsumer's replica configuration. Event type is DeadLetterEnvelope
// because PaymentConsumer runs with ExtendDeadletterMessage = true.
public class PaymentAlertsConsumer(ILogger<PaymentAlertsConsumer> logger) : IRabbitFlowConsumer<DeadLetterEnvelope>
{
    public Task HandleAsync(DeadLetterEnvelope envelope, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "[PaymentAlerts] Dead-lettered payment detected. MessageType={MessageType} Exception={ExceptionType} Error={ErrorMessage} OriginalMessageId={OriginalMessageId}",
            envelope.MessageType,
            envelope.ExceptionType ?? "(unknown)",
            envelope.ErrorMessage ?? "(no error message)",
            envelope.MessageId ?? "(none)");

        // Real-world hook: send to Slack/PagerDuty/email, write to alerting DB, etc.

        return Task.CompletedTask;
    }
}
