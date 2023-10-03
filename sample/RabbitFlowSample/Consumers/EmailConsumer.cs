using System.Text.Json;
using RabbitFlow.Services;
using RabbitFlowSample.Events;

namespace RabbitFlowSample.Consumers;

public class EmailConsumer : IRabbitFlowConsumer<EmailEvent>
{
    private readonly ILogger<EmailConsumer> _logger;

    public EmailConsumer(ILogger<EmailConsumer> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(EmailEvent message, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        _logger.LogInformation("New email event received. Event:{event}", JsonSerializer.Serialize(message));
    }
}
