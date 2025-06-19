using EasyRabbitFlow.Services;
using RabbitFlowSample.Events;
using System.Text.Json;

namespace RabbitFlowSample.Consumers;

public class WhatsAppConsumer : IRabbitFlowConsumer<NotificationEvent>
{
    private readonly ILogger<WhatsAppConsumer> _logger;

    public WhatsAppConsumer(ILogger<WhatsAppConsumer> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(NotificationEvent message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("New whatsapp event received. Event:{event}", JsonSerializer.Serialize(message));

        await Task.Delay(1000, cancellationToken);
    }
}
