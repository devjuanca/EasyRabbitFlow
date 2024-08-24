using EasyRabbitFlow.Services;
using RabbitFlowFanoutSample.Events;
using System.Text.Json;

namespace RabbitFlowFanoutSample.Consumers;

public class WhatsAppConsumer : IRabbitFlowConsumer<NotificationEvent>
{
    private readonly ILogger<WhatsAppConsumer> _logger;

    public WhatsAppConsumer(ILogger<WhatsAppConsumer> logger)
    {
        _logger = logger;
    }

    public Task HandleAsync(NotificationEvent message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("New whatsapp event received. Event:{event}", JsonSerializer.Serialize(message));

        return Task.CompletedTask;
    }
}
