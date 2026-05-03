using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using RabbitFlowSample.Events;

namespace RabbitFlowSample.Consumers;

// Topic binding: "orders.*.created" — receives ONLY "created" events, regardless of region.
// "*" matches exactly one word, so "orders.eu.created" and "orders.us.created" both arrive,
// while "orders.eu.shipped" or "orders.eu.created.priority" do NOT.
public class OrderCreatedConsumer(ILogger<OrderCreatedConsumer> logger) : IRabbitFlowConsumer<OrderEvent>
{
    public Task HandleAsync(OrderEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[NEW-ORDER] region={Region} order={OrderId} customer={Email} amount={Amount} (routingKey={Key})",
            message.Region, message.OrderId, message.CustomerEmail ?? "(anon)", message.Amount, context.RoutingKey);

        return Task.CompletedTask;
    }
}
