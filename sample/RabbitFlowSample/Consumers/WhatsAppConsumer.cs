using EasyRabbitFlow.Services;
using RabbitFlowSimpleSample.Events;
using System.Text.Json;

namespace RabbitFlowSimpleSample.Consumers;

public class WhatsAppConsumer : IRabbitFlowConsumer<WhatsAppEvent>
{
    private readonly ILogger<WhatsAppConsumer> _logger;

    private readonly GuidSevice _guidSevice;

    public WhatsAppConsumer(ILogger<WhatsAppConsumer> logger, GuidSevice guidSevice)
    {
        _logger = logger;

        _guidSevice = guidSevice;
    }

    public Task HandleAsync(WhatsAppEvent message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("New whatsapp event received. Event:{event} Id: {id}", JsonSerializer.Serialize(message), _guidSevice.Guid);

        return Task.CompletedTask;
    }
}
