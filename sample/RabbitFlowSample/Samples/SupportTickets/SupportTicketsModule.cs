using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using Microsoft.AspNetCore.Mvc;

namespace RabbitFlowSample.Samples.SupportTickets;

// Priority queue sample. The queue is declared with x-max-priority=9 so the broker
// re-orders pending messages by AMQP priority; the publisher maps each ticket's
// logical severity to a numeric priority via PublishOptions.Priority.
public static class SupportTicketsModule
{
    public const string MainQueue = "support-tickets-queue";

    // Top of the priority window (0..9). Must match the value passed to MaxPriority
    // when declaring the queue, otherwise high values get capped by the broker.
    public const byte MaxPriority = 9;

    public static void RegisterConsumers(RabbitFlowConfigurator settings)
    {
        settings.AddConsumer<SupportTicketConsumer>(queueName: MainQueue, c =>
        {
            c.AutoGenerate = true;
            c.PrefetchCount = 1;                          // process one ticket at a time so priority ordering is observable
            c.Timeout = TimeSpan.FromSeconds(10);
            c.ExtendDeadletterMessage = true;

            c.ConfigureAutoGenerate(ag =>
            {
                ag.GenerateExchange = true;
                ag.GenerateDeadletterQueue = true;
                ag.MaxPriority = MaxPriority;             // declares "x-max-priority" on the queue — required for Priority to take effect
            });
        });
    }

    public static void MapEndpoints(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/support/tickets").WithTags("SupportTickets");

        // Submit a single ticket. The severity in the body is mapped to an AMQP
        // priority via MapPriority; the broker honors it because the queue was
        // declared with MaxPriority.
        group.MapPost("/", async (
            [FromServices] IRabbitFlowPublisher publisher,
            [FromBody] SupportTicketEvent ticket) =>
        {
            var priority = MapPriority(ticket.Severity);

            var result = await publisher.PublishAsync(
                ticket,
                queueName: MainQueue,
                messageId: $"ticket-{ticket.TicketId}",
                options: new PublishOptions { Priority = priority });

            return result.Success
                ? Results.Accepted(value: new { result.MessageId, AmqpPriority = priority, Payload = ticket })
                : Results.Problem(detail: result.Error?.Message, statusCode: StatusCodes.Status500InternalServerError);
        })
        .WithName("PublishSupportTicket")
        .WithSummary("Publishes a support ticket with AMQP priority derived from its Severity (Low|Normal|High|Critical).")
        .Produces(StatusCodes.Status202Accepted);

        // Burst endpoint — publishes tickets grouped by severity in the order
        // low → normal → high → critical. With a slow consumer and PrefetchCount=1,
        // the consumer drains them in the OPPOSITE order (critical first), proving
        // that the priority queue is re-ordering messages on the broker side.
        group.MapPost("/burst", async (
            [FromServices] IRabbitFlowPublisher publisher,
            [FromBody] TicketBurstRequest request) =>
        {
            var batches = new (TicketSeverity Severity, int Count)[]
            {
                (TicketSeverity.Low, request.LowCount),
                (TicketSeverity.Normal, request.NormalCount),
                (TicketSeverity.High, request.HighCount),
                (TicketSeverity.Critical, request.CriticalCount),
            };

            var published = 0;

            foreach (var (severity, count) in batches)
            {
                if (count <= 0)
                {
                    continue;
                }

                var priority = MapPriority(severity);

                for (var i = 0; i < count; i++)
                {
                    var ticket = new SupportTicketEvent
                    {
                        CustomerEmail = $"customer{i}@example.com",
                        Severity = severity,
                        Subject = $"{severity} ticket #{i + 1}",
                    };

                    var result = await publisher.PublishAsync(
                        ticket,
                        queueName: MainQueue,
                        messageId: $"ticket-{ticket.TicketId}",
                        options: new PublishOptions { Priority = priority });

                    if (result.Success)
                    {
                        published++;
                    }
                }
            }

            return Results.Accepted(value: new
            {
                requested = batches.Sum(b => b.Count),
                published,
                hint = "Watch the consumer logs: tickets drain Critical → High → Normal → Low even though they were inserted in the opposite order."
            });
        })
        .WithName("PublishSupportTicketBurst")
        .WithSummary("Publishes a burst of tickets grouped by severity from lowest to highest. The consumer drains them by AMQP priority, so Critical comes out first.")
        .Produces(StatusCodes.Status202Accepted);
    }

    // Logical-to-AMQP priority mapping. Kept centralized so the wire format never
    // leaks into the payload and so it can be tuned in one place.
    private static byte MapPriority(TicketSeverity severity) => severity switch
    {
        TicketSeverity.Critical => 9,
        TicketSeverity.High => 6,
        TicketSeverity.Normal => 3,
        TicketSeverity.Low => 1,
        _ => 0,
    };
}
