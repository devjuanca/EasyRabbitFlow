using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using RabbitFlowSample.Events;

namespace RabbitFlowSample.Consumers;

// Topic binding: "orders.#" — receives EVERY order event, every region, every status.
// "#" matches zero or more dotted words, so this is the audit-log pattern: it will see
// "orders.eu.created", "orders.us.shipped.priority", and even just "orders" itself.
public class OrderAuditConsumer(ILogger<OrderAuditConsumer> logger) : IRabbitFlowConsumer<OrderEvent>
{
    public Task HandleAsync(OrderEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[AUDIT] {Key} → order={OrderId} region={Region} status={Status} amount={Amount}",
            context.RoutingKey, message.OrderId, message.Region, message.Status, message.Amount);

        return Task.CompletedTask;
    }
}
