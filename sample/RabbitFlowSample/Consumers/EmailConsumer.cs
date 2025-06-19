using EasyRabbitFlow.Services;
using RabbitFlowSample.Events;
using System.Text.Json;

namespace RabbitFlowSample.Consumers;

public class EmailConsumer : IRabbitFlowConsumer<NotificationEvent>
{
    private readonly ILogger<EmailConsumer> _logger;

    public EmailConsumer(ILogger<EmailConsumer> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(NotificationEvent message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("New email event received. Event:{event}", JsonSerializer.Serialize(message));

        await Task.Delay(1000, cancellationToken);
    }
}
