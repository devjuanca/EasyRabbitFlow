using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using RabbitFlowSample.Events;

namespace RabbitFlowSample.Consumers;

// Topic binding: "orders.eu.*" — receives EVERY status for the EU region only.
// Will see "orders.eu.created", "orders.eu.shipped", "orders.eu.cancelled",
// but NOT "orders.us.created" or any non-EU routing key.
public class EuOrdersConsumer(ILogger<EuOrdersConsumer> logger) : IRabbitFlowConsumer<OrderEvent>
{
    public Task HandleAsync(OrderEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[EU-ORDERS] routingKey={Key} order={OrderId} status={Status} amount={Amount}",
            context.RoutingKey, message.OrderId, message.Status, message.Amount);

        return Task.CompletedTask;
    }
}
