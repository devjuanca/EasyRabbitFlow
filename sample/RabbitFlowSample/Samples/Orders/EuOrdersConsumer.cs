using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;

namespace RabbitFlowSample.Samples.Orders;

// Topic binding: "orders.eu.*" — every status for the EU region only.
// Receives:  "orders.eu.created", "orders.eu.shipped", "orders.eu.cancelled"
// Skips:     "orders.us.created", "orders.ap.*", anything non-EU.
public sealed class EuOrdersConsumer(ILogger<EuOrdersConsumer> logger) : IRabbitFlowConsumer<OrderEvent>
{
    public Task HandleAsync(OrderEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[EU-ORDERS] routingKey={Key} order={OrderId} status={Status} amount={Amount}",
            context.RoutingKey, message.OrderId, message.Status, message.Amount);

        return Task.CompletedTask;
    }
}
