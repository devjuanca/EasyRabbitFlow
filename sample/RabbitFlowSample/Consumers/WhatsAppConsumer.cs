using EasyRabbitFlow.Exceptions;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using RabbitFlowSample.Events;
using System.Text.Json;

namespace RabbitFlowSample.Consumers;

public class WhatsAppConsumer(
    GuidScopedService guidScopedService,
    GuidSingletonService guidSingletonService,
    GuidTransientService guidTransientService,
    ILogger<WhatsAppConsumer> logger) : IRabbitFlowConsumer<NotificationEvent>
{
    public async Task HandleAsync(NotificationEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        var scopedGuid = guidScopedService.Guid;

        var singletonGuid = guidSingletonService.Guid;

        var transientGuid = guidTransientService.Guid;

        //logger.LogInformation("[WhatsApp] Singleton service GUID: {Guid}", singletonGuid);

        //logger.LogInformation("[WhatsApp] Scoped service GUID: {Guid}", scopedGuid);

        //logger.LogInformation("[WhatsApp] Transient service GUID: {Guid}", transientGuid);

        if (message.WhatsAppNotificationData is null)
        {
            logger.LogInformation("[WhatsApp] No whatsapp data. Skipping.");
            return;
        }

        if (context.ReprocessAttempts > 0)
        {
            logger.LogInformation("Reprocessing message (attempt {N}) after a previous failure.", context.ReprocessAttempts);
        }

        var roll = Random.Shared.NextDouble();

        if (roll < 0.5)
        {
            logger.LogWarning("[WhatsApp] Transient failure encountered. Will be retried if configured.");
            throw new RabbitFlowTransientException("Simulated transient email send failure.");
        }
        
        var latency = Random.Shared.Next(50, 250);
        
        await Task.Delay(latency, cancellationToken); // Simulate IO

        logger.LogInformation("[WhatsApp] Delivered whatsapp message after {Latency}ms: {Payload}", latency, JsonSerializer.Serialize(message.WhatsAppNotificationData));
    }
}