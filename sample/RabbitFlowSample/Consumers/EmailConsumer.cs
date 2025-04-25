using EasyRabbitFlow.Services;
using RabbitFlowSimpleSample.Events;
using System.Text.Json;

namespace RabbitFlowSimpleSample.Consumers;

public class EmailConsumer : IRabbitFlowConsumer<EmailEvent>
{
    private readonly ILogger<EmailConsumer> _logger;

    private readonly GuidSevice _guidSevice;

    public EmailConsumer(ILogger<EmailConsumer> logger, GuidSevice guidSevice)
    {
        _guidSevice = guidSevice;

        _logger = logger;
    }

    public Task HandleAsync(EmailEvent message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("New email event received. Event:{event}. Id: {id}", JsonSerializer.Serialize(message), _guidSevice.Guid);

        return Task.CompletedTask;
    }
}
