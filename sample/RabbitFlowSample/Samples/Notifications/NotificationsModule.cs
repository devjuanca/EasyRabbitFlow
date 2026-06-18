using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using Microsoft.AspNetCore.Mvc;

namespace RabbitFlowSample.Samples.Notifications;

// Wires the Notifications sample into the host: registers both consumers against
// the auto-generated "notifications" fanout exchange and exposes the publish endpoints.
public static class NotificationsModule
{
    public const string ExchangeName = "notifications";
    
    public const string EmailQueue = "email-queue";
    
    public const string WhatsAppQueue = "whatsapp-queue";

    public static void RegisterConsumers(RabbitFlowConfigurator settings)
    {
        // Both consumers bind to the same Fanout exchange, so a single publish
        // is delivered to BOTH queues simultaneously — classic pub/sub broadcast.

        settings.AddConsumer<EmailConsumer>(queueName: EmailQueue, c =>
        {
            c.ConsumerId = "EmailQueueConsumer";
            c.Timeout = TimeSpan.FromSeconds(30);
            c.AutoGenerate = true;

            c.ConfigureAutoGenerate(opt =>
            {
                opt.ExchangeName = ExchangeName;
                opt.ExchangeType = ExchangeType.Fanout;
            });

            c.ConfigureRetryPolicy(r =>
            {
                r.MaxRetryCount = 1;             // one retry after the initial attempt
                r.RetryInterval = 1000;         // fixed, ephemeral delay between retries
            });

            c.ExtendDeadletterMessage = true;    // wrap failed messages in DeadLetterEnvelope
            c.UnwrapDeadLetterEnvelopes = true;  // accept envelope-wrapped messages on manual DLQ replay
        });

        settings.AddConsumer<WhatsAppConsumer>(queueName: WhatsAppQueue, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(60);
            c.AutoGenerate = true;

            c.ConfigureAutoGenerate(opt =>
            {
                opt.ExchangeName = ExchangeName;
                opt.ExchangeType = ExchangeType.Fanout;
            });

            c.ExtendDeadletterMessage = true;

            // Dead-letter reprocessor: after a message lands on the DLQ, re-inject it
            // into the main queue every hour, up to 2 attempts.
            c.ConfigureDeadLetterReprocess(opt =>
            {
                opt.Enabled = true;
                opt.MaxReprocessAttempts = 2;
                opt.Interval = TimeSpan.FromHours(1);
            });
        });
    }

    public static void MapEndpoints(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/notifications").WithTags("Notifications");

        // Fan out a single event to BOTH email and whatsapp consumers via the fanout exchange.
        group.MapPost("/broadcast", async (
            [FromServices] IRabbitFlowPublisher publisher,
            [FromBody] NotificationEvent payload) =>
        {
            var result = await publisher.PublishAsync(
                payload,
                exchangeName: ExchangeName,
                routingKey: "",
                messageId: $"notification-{Guid.NewGuid():N}");

            return result.Success
                ? Results.Accepted(value: new { result.MessageId, result.Destination, result.TimestampUtc, Payload = payload })
                : Results.Problem(detail: result.Error?.Message, statusCode: StatusCodes.Status500InternalServerError);
        })
        .WithName("BroadcastNotification")
        .WithSummary("Broadcasts a NotificationEvent to the 'notifications' fanout exchange (reaches email + whatsapp consumers).")
        .Produces(StatusCodes.Status202Accepted);

        // Publish directly to the email queue, bypassing the fanout exchange.
        group.MapPost("/email", async (
            [FromServices] IRabbitFlowPublisher publisher,
            [FromBody] NotificationEvent payload) =>
        {
            var result = await publisher.PublishAsync(
                payload,
                queueName: EmailQueue,
                correlationId: Guid.NewGuid().ToString("N"));

            return result.Success
                ? Results.Accepted(value: new { result.MessageId, result.Destination, result.TimestampUtc, Payload = payload })
                : Results.Problem(detail: result.Error?.Message, statusCode: StatusCodes.Status500InternalServerError);
        })
        .WithName("PublishEmailDirect")
        .WithSummary("Publishes a NotificationEvent directly to the email queue, bypassing the fanout exchange.")
        .Produces(StatusCodes.Status202Accepted);

        // Publish directly to the whatsapp queue, bypassing the fanout exchange.
        group.MapPost("/whatsapp", async (
            [FromServices] IRabbitFlowPublisher publisher,
            [FromBody] NotificationEvent payload) =>
        {
            var result = await publisher.PublishAsync(payload, queueName: WhatsAppQueue);

            return result.Success
                ? Results.Accepted(value: new { result.MessageId, result.Destination, result.TimestampUtc, Payload = payload })
                : Results.Problem(detail: result.Error?.Message, statusCode: StatusCodes.Status500InternalServerError);
        })
        .WithName("PublishWhatsAppDirect")
        .WithSummary("Publishes a NotificationEvent directly to the whatsapp queue, bypassing the fanout exchange.")
        .Produces(StatusCodes.Status202Accepted);

        // Atomic batch publish to the fanout exchange. Default channel mode is Transactional:
        // if any single message fails, the whole batch is rolled back and nothing is delivered.
        group.MapPost("/broadcast/batch", async (
            [FromServices] IRabbitFlowPublisher publisher,
            [FromBody] NotificationEvent[] payloads) =>
        {
            var result = await publisher.PublishBatchAsync(
                payloads,
                exchangeName: ExchangeName,
                routingKey: "",
                messageIdSelector: _ => $"notification-{Guid.NewGuid():N}");

            return result.Success
                ? Results.Accepted(value: new { result.MessageCount, result.MessageIds, result.Destination, result.ChannelMode, result.TimestampUtc })
                : Results.Problem(detail: result.Error?.Message, statusCode: StatusCodes.Status500InternalServerError);
        })
        .WithName("BroadcastNotificationBatch")
        .WithSummary("Atomically publishes a batch of NotificationEvents to the fanout exchange (Transactional mode).")
        .Produces(StatusCodes.Status202Accepted);
    }
}
