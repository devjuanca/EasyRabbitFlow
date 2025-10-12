using EasyRabbitFlow.Services;
using EasyRabbitFlow.Exceptions;
using RabbitFlowSample.Events;
using System.Text.Json;

namespace RabbitFlowSample.Consumers;

public class EmailConsumer(ILogger<EmailConsumer> logger) : IRabbitFlowConsumer<NotificationEvent>
{
    public async Task HandleAsync(NotificationEvent message, CancellationToken cancellationToken)
    {
        if (message.EmailNotificationData is null)
        {
            logger.LogInformation("[EmailSend] No email data. Skipping.");
            return;
        }
        // Simulate random transient failure
        var roll = Random.Shared.NextDouble();

        if (roll < 0.15)
        {
            logger.LogWarning("[EmailSend] Transient failure encountered. Will be retried if configured.");
            throw new RabbitFlowTransientException("Simulated transient email send failure.");
        }
        if (roll > 0.95)
        {
            logger.LogError("[EmailSend] Permanent failure encountered. Will not retry.");
            throw new Exception("Simulated permanent email failure.");
        }
        
        await Task.Delay(300, cancellationToken); // Simulate IO
        
        logger.LogInformation("[EmailSend] Email sent successfully: {Payload}", JsonSerializer.Serialize(message.EmailNotificationData));
    }
}