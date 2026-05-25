using EasyRabbitFlow.Exceptions;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using RabbitFlowSample.Common;
using System.Text.Json;

namespace RabbitFlowSample.Samples.Notifications;

// Subscribes to the "notifications" fanout exchange via the "whatsapp-queue".
// Demonstrates DI lifetimes (singleton/scoped/transient — see GuidService.cs) and the
// dead-letter reprocessor: after the retry policy exhausts attempts, the message is
// dead-lettered and later re-injected into the main queue by the reprocessor.
public sealed class WhatsAppConsumer(
    GuidScopedService guidScopedService,
    GuidSingletonService guidSingletonService,
    GuidTransientService guidTransientService,
    ILogger<WhatsAppConsumer> logger) : IRabbitFlowConsumer<NotificationEvent>
{
    public async Task HandleAsync(NotificationEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[WhatsApp] DI lifetimes — singleton={Singleton}, scoped={Scoped}, transient={Transient}",
            guidSingletonService.Guid, guidScopedService.Guid, guidTransientService.Guid);

        if (message.WhatsAppNotificationData is null)
        {
            logger.LogInformation("[WhatsApp] No whatsapp data in payload. Skipping.");
            return;
        }

        if (context.ReprocessAttempts > 0)
        {
            logger.LogInformation("[WhatsApp] Reprocessing message (attempt {N}) after a previous DLQ visit.", context.ReprocessAttempts);
        }

        // High failure rate (50%) so the dead-letter reprocessor visibly kicks in.
        if (Random.Shared.NextDouble() < 0.5)
        {
            logger.LogWarning("[WhatsApp] Simulated transient failure. Will be retried, then dead-lettered if retries exhaust.");
            throw new RabbitFlowTransientException("Simulated transient whatsapp-send failure.");
        }

        var latency = Random.Shared.Next(50, 250);

        await Task.Delay(latency, cancellationToken);

        logger.LogInformation("[WhatsApp] Delivered after {Latency}ms: {Payload}", latency, JsonSerializer.Serialize(message.WhatsAppNotificationData));
    }
}
