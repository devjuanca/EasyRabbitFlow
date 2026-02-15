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

        logger.LogInformation("[WhatsApp] Singleton service GUID: {Guid}", singletonGuid);

        logger.LogInformation("[WhatsApp] Scoped service GUID: {Guid}", scopedGuid);

        logger.LogInformation("[WhatsApp] Transient service GUID: {Guid}", transientGuid);

        if (message.WhatsAppNotificationData is null)
        {
            logger.LogInformation("[WhatsApp] No whatsapp data. Skipping.");
            return;
        }

        var latency = Random.Shared.Next(50, 250);
        
        await Task.Delay(latency, cancellationToken);
        
        logger.LogInformation("[WhatsApp] Delivered whatsapp message after {Latency}ms: {Payload}", latency, JsonSerializer.Serialize(message.WhatsAppNotificationData));
    }
}