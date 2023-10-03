using System.Text.Json;
using RabbitFlow.Services;
using RabbitFlowSample.Events;

namespace RabbitFlowSample.Consumers;

public class WhatsAppConsumer : IRabbitFlowConsumer<WhatsAppEvent>
{
    private readonly ILogger<EmailConsumer> _logger;

    public WhatsAppConsumer(ILogger<EmailConsumer> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(WhatsAppEvent message, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        _logger.LogInformation("New whatsapp event received. Event:{event}", JsonSerializer.Serialize(message));
    }
}
