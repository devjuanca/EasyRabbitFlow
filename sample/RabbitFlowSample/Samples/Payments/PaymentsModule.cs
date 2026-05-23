using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using Microsoft.AspNetCore.Mvc;

namespace RabbitFlowSample.Samples.Payments;

// Dead-letter replica sample. PaymentConsumer auto-generates its main queue plus the
// dead-letter topology; DeadLetterReplicas attach two extra queues to the dead-letter
// exchange, so every dead-lettered message is delivered as a copy to:
//
//   payments-queue-deadletter   primary DLQ (could be drained by the reprocessor)
//   payments-audit              long-retention replica (30-day TTL), inspected manually
//   payments-alerts             consumed live by PaymentAlertsConsumer
//
// Try POST /payments with shouldFail=true (or amount<=0) to force a failure and watch
// the message appear in all three queues. The alerts consumer logs each dead-letter
// event in real time; the audit replica stays put for later review.
public static class PaymentsModule
{
    public const string MainQueue = "payments-queue";
    
    public const string AuditReplica = "payments-audit";
    
    public const string AlertsReplica = "payments-alerts";

    public static void RegisterConsumers(RabbitFlowConfigurator settings)
    {
        // IMPORTANT: PaymentConsumer must be registered before PaymentAlertsConsumer
        // because it owns the declaration of the payments-alerts replica queue.

        settings.AddConsumer<PaymentConsumer>(queueName: MainQueue, c =>
        {
            c.AutoGenerate = true;
            c.ExtendDeadletterMessage = true;   // dead-letter copies carry the full DeadLetterEnvelope

            c.ConfigureRetryPolicy(r =>
            {
                r.MaxRetryCount = 1;            // one retry after the initial attempt
                r.RetryInterval = 500;
            });

            c.ConfigureAutoGenerate(ag =>
            {
                ag.GenerateDeadletterQueue = true;   // required for replicas to bind

                // Long-retention replica. Nothing consumes this — operators inspect it via the management UI.
                ag.DeadLetterReplicas.Add(new DeadLetterReplica
                {
                    QueueName = AuditReplica,
                    Arguments = new Dictionary<string, object?>
                    {
                        ["x-message-ttl"] = (long)TimeSpan.FromDays(30).TotalMilliseconds
                    }
                });

                // Live alerting queue — see PaymentAlertsConsumer.
                ag.DeadLetterReplicas.Add(new DeadLetterReplica
                {
                    QueueName = AlertsReplica
                });
            });
        });

        settings.AddConsumer<PaymentAlertsConsumer>(queueName: AlertsReplica, c =>
        {
            c.AutoGenerate = false;             // queue is declared by PaymentConsumer's replica config
            c.ExtendDeadletterMessage = false;  // alerts queue does not need its own DLQ
        });
    }

    public static void MapEndpoints(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/payments").WithTags("Payments");

        // Publish a PaymentEvent. Body controls the outcome:
        //   shouldFail=false AND amount>0 → processed; no replica traffic.
        //   shouldFail=true   OR amount<=0 → dead-lettered after retries, copies land in all replicas.
        group.MapPost("/", async (
            [FromServices] IRabbitFlowPublisher publisher,
            [FromBody] PaymentEvent payment) =>
        {
            var result = await publisher.PublishAsync(
                payment,
                queueName: MainQueue,
                messageId: $"payment-{payment.PaymentId}");

            return result.Success
                ? Results.Accepted(value: new { result.MessageId, result.Destination, Payload = payment })
                : Results.Problem(detail: result.Error?.Message, statusCode: StatusCodes.Status500InternalServerError);
        })
        .WithName("PublishPaymentEvent")
        .WithSummary("Publishes a PaymentEvent. Set shouldFail=true (or amount<=0) to dead-letter the message and observe replicas in payments-audit + payments-alerts.")
        .Produces(StatusCodes.Status202Accepted);
    }
}
