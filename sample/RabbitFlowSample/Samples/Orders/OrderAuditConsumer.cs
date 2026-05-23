using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;

namespace RabbitFlowSample.Samples.Orders;

// Topic binding: "orders.#" — every order event, every region, every status.
// "#" matches zero or more dotted words, so this is the catch-all audit pattern:
// "orders.eu.created", "orders.us.shipped.priority", and even bare "orders" all arrive here.
public sealed class OrderAuditConsumer(ILogger<OrderAuditConsumer> logger) : IRabbitFlowConsumer<OrderEvent>
{
    public Task HandleAsync(OrderEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[AUDIT] {Key} → order={OrderId} region={Region} status={Status} amount={Amount}",
            context.RoutingKey, message.OrderId, message.Region, message.Status, message.Amount);

        return Task.CompletedTask;
    }
}
