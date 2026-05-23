using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using Microsoft.AspNetCore.Mvc;

namespace RabbitFlowSample.Samples.Orders;

// Topic exchange sample: three consumers share the same "orders-topic" exchange but
// bind to different patterns. Producers publish with concrete routing keys shaped
// "orders.{region}.{status}"; the broker dispatches each message to whichever queues
// have a binding that matches, courtesy of the Topic wildcards * (one word) and # (any words).
public static class OrdersModule
{
    public const string ExchangeName = "orders-topic";
    
    public const string EuQueue = "orders-eu-queue";
    
    public const string CreatedQueue = "orders-created-queue";
    
    public const string AuditQueue = "orders-audit-queue";

    private static readonly string[] Regions = { "eu", "us", "ap" };

    private static readonly string[] Statuses = { "created", "shipped", "cancelled" };

    public static void RegisterConsumers(RabbitFlowConfigurator settings)
    {
        settings.AddConsumer<EuOrdersConsumer>(queueName: EuQueue, c =>
        {
            c.AutoGenerate = true;
            c.ConfigureAutoGenerate(opt =>
            {
                opt.ExchangeName = ExchangeName;
                opt.ExchangeType = ExchangeType.Topic;
                opt.RoutingKey = "orders.eu.*";        // every status, EU only
            });
        });

        settings.AddConsumer<OrderCreatedConsumer>(queueName: CreatedQueue, c =>
        {
            c.AutoGenerate = true;
            c.ConfigureAutoGenerate(opt =>
            {
                opt.ExchangeName = ExchangeName;
                opt.ExchangeType = ExchangeType.Topic;
                opt.RoutingKey = "orders.*.created";   // "created" status, any region
            });
        });

        settings.AddConsumer<OrderAuditConsumer>(queueName: AuditQueue, c =>
        {
            c.AutoGenerate = true;
            c.ConfigureAutoGenerate(opt =>
            {
                opt.ExchangeName = ExchangeName;
                opt.ExchangeType = ExchangeType.Topic;
                opt.RoutingKey = "orders.#";           // catch-all (audit log)
            });
        });
    }

    public static void MapEndpoints(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/orders").WithTags("Orders");

        // Publish a single OrderEvent. The path segments build the routing key
        // "orders.{region}.{status}", and the broker dispatches it to whichever
        // bindings match. Try the following to see the patterns in action:
        //   POST /orders/eu/created     → audit + EU + created
        //   POST /orders/us/created     → audit + created    (NOT eu)
        //   POST /orders/eu/shipped     → audit + EU         (NOT created)
        //   POST /orders/ap/cancelled   → audit only         (no EU, no created)
        group.MapPost("/{region}/{status}", async (
            string region,
            string status,
            [FromServices] IRabbitFlowPublisher publisher,
            [FromBody] OrderEvent payload) =>
        {
            payload.Region = region;
            payload.Status = status;

            var routingKey = $"orders.{region.ToLowerInvariant()}.{status.ToLowerInvariant()}";

            var result = await publisher.PublishAsync(
                payload,
                exchangeName: ExchangeName,
                routingKey: routingKey,
                messageId: $"order-{payload.OrderId}-{status.ToLowerInvariant()}");

            return result.Success
                ? Results.Accepted(value: new { result.MessageId, result.Destination, RoutingKey = routingKey, Payload = payload })
                : Results.Problem(detail: result.Error?.Message, statusCode: StatusCodes.Status500InternalServerError);
        })
        .WithName("PublishOrderEvent")
        .WithSummary("Publishes an OrderEvent to the 'orders-topic' exchange with routing key 'orders.{region}.{status}'.")
        .Produces(StatusCodes.Status202Accepted);

        // Bulk publish a random mix of regions/statuses. Useful for watching the three
        // consumers light up at different rates depending on their binding.
        group.MapPost("/random/{count:int}", async (
            int count,
            [FromServices] IRabbitFlowPublisher publisher) =>
        {
            var rng = Random.Shared;
            var produced = new List<object>(count);

            for (var i = 0; i < count; i++)
            {
                var region = Regions[rng.Next(Regions.Length)];
                var status = Statuses[rng.Next(Statuses.Length)];

                var order = new OrderEvent
                {
                    Region = region,
                    Status = status,
                    CustomerEmail = $"customer{i}@example.com",
                    Amount = Math.Round((decimal)(rng.NextDouble() * 500), 2),
                };

                var routingKey = $"orders.{region}.{status}";

                var result = await publisher.PublishAsync(
                    order,
                    exchangeName: ExchangeName,
                    routingKey: routingKey,
                    messageId: $"order-{order.OrderId}-{status}");

                produced.Add(new { result.MessageId, RoutingKey = routingKey, order.OrderId, order.Amount });
            }

            return Results.Accepted(value: new { count = produced.Count, items = produced });
        })
        .WithName("PublishOrderEventRandomBurst")
        .WithSummary("Publishes a random burst of OrderEvents across regions and statuses to the 'orders-topic' exchange.")
        .Produces(StatusCodes.Status202Accepted);
    }
}
