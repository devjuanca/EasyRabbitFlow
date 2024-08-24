using EasyRabbitFlow.Services;
using RabbitFlowFanoutSample.Events;
using System.Text.Json;

namespace RabbitFlowFanoutSample.Consumers;

public class EmailConsumer : IRabbitFlowConsumer<NotificationEvent>
{
    private readonly ILogger<EmailConsumer> _logger;

    public EmailConsumer(ILogger<EmailConsumer> logger)
    {
        _logger = logger;
    }

    public Task HandleAsync(NotificationEvent message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("New email event received. Event:{event}", JsonSerializer.Serialize(message));

        return Task.CompletedTask;
    }
}
