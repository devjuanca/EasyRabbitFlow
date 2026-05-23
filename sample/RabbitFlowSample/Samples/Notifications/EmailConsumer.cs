using EasyRabbitFlow.Exceptions;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using System.Text.Json;

namespace RabbitFlowSample.Samples.Notifications;

// Subscribes to the "notifications" fanout exchange via the "email-queue".
// Also receives messages published directly to "email-queue" by POST /notifications/email.
// Demonstrates: random transient failure (retried) vs. random permanent failure (dead-lettered).
public sealed class EmailConsumer(ILogger<EmailConsumer> logger) : IRabbitFlowConsumer<NotificationEvent>
{
    public async Task HandleAsync(NotificationEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[Email] Processing message. MessageId={MessageId}, Redelivered={Redelivered}, ReprocessAttempts={ReprocessAttempts}",
            context.MessageId ?? "(none)", context.Redelivered, context.ReprocessAttempts);

        if (message.EmailNotificationData is null)
        {
            logger.LogInformation("[Email] No email data in payload. Skipping.");
            return;
        }

        var roll = Random.Shared.NextDouble();

        // 15% chance: throws a transient exception → EasyRabbitFlow retries per the consumer's RetryPolicy.
        if (roll < 0.15)
        {
            logger.LogWarning("[Email] Simulated transient failure. Will be retried.");
            throw new RabbitFlowTransientException("Simulated transient email-send failure.");
        }

        // 5% chance: throws a regular exception → dead-lettered immediately (no retry).
        if (roll > 0.95)
        {
            logger.LogError("[Email] Simulated permanent failure. Will be dead-lettered.");
            throw new InvalidOperationException("Simulated permanent email-send failure.");
        }

        await Task.Delay(300, cancellationToken);

        logger.LogInformation("[Email] Sent successfully: {Payload}", JsonSerializer.Serialize(message.EmailNotificationData));
    }
}
