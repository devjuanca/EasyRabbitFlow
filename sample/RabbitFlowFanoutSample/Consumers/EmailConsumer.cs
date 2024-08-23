using RabbitFlow.Services;
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
        await Task.CompletedTask;

        _logger.LogInformation("New email event received. Event:{event}", JsonSerializer.Serialize(message));
    }
}
