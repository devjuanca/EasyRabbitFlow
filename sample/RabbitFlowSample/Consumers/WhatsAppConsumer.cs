using RabbitFlow.Services;
using RabbitFlowSample.Events;
using System.Text.Json;

namespace RabbitFlowSample.Consumers;

public class WhatsAppConsumer : IRabbitFlowConsumer<WhatsAppEvent>
{
    private readonly ILogger<WhatsAppConsumer> _logger;

    public WhatsAppConsumer(ILogger<WhatsAppConsumer> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(WhatsAppEvent message, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        _logger.LogInformation("New whatsapp event received. Event:{event}", JsonSerializer.Serialize(message));
    }
}
