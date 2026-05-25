using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;

namespace RabbitFlowSample.Samples.Payments;

// Main payment consumer. Demonstrates DeadLetterReplicas: when a message fails here,
// RabbitMQ routes it via the auto-generated dead-letter exchange to every queue bound
// to that exchange — the primary DLQ plus every queue listed in
// AutoGenerateSettings.DeadLetterReplicas.
public sealed class PaymentConsumer(ILogger<PaymentConsumer> logger) : IRabbitFlowConsumer<PaymentEvent>
{
    public async Task HandleAsync(PaymentEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[Payment] Processing {PaymentId} for {Amount} {Currency} (MessageId={MessageId})",
            message.PaymentId, message.Amount, message.Currency, context.MessageId ?? "(none)");

        if (message.ShouldFail)
        {
            logger.LogError("[Payment] Forced-failure flag set on {PaymentId}. Will be dead-lettered.", message.PaymentId);
            throw new InvalidOperationException("Forced failure for dead-letter replica demo.");
        }

        if (message.Amount <= 0)
        {
            logger.LogError("[Payment] Invalid amount {Amount} on {PaymentId}. Will be dead-lettered.", message.Amount, message.PaymentId);
            throw new ArgumentOutOfRangeException(nameof(message.Amount), "Amount must be positive.");
        }

        await Task.Delay(200, cancellationToken);

        logger.LogInformation("[Payment] {PaymentId} processed successfully.", message.PaymentId);
    }
}
