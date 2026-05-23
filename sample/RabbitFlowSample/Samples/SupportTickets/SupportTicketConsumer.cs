using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;

namespace RabbitFlowSample.Samples.SupportTickets;

// Drains the priority queue one ticket at a time with deliberate latency, so the
// queue stays loaded long enough for priority ordering to be visible in the logs.
// Logs the AMQP priority alongside the severity so you can verify the broker
// delivers higher-priority tickets first while a backlog exists.
public sealed class SupportTicketConsumer(ILogger<SupportTicketConsumer> logger) : IRabbitFlowConsumer<SupportTicketEvent>
{
    public async Task HandleAsync(SupportTicketEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[SupportTicket] Handling {TicketId} severity={Severity} amqpPriority={Priority} subject=\"{Subject}\"",
            message.TicketId,
            message.Severity,
            context.Priority?.ToString() ?? "(none)",
            message.Subject);

        // Simulate triage work. Slow on purpose: with PrefetchCount=1 the broker
        // can only release the next ticket once this one finishes, so the queue
        // accumulates and the priority ordering becomes obvious.
        await Task.Delay(TimeSpan.FromMilliseconds(800), cancellationToken);

        logger.LogInformation("[SupportTicket] Closed {TicketId}", message.TicketId);
    }
}
