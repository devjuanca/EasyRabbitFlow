using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using Microsoft.AspNetCore.Mvc;
using RabbitMQ.Client;
using ExchangeType = EasyRabbitFlow.Settings.ExchangeType;

namespace RabbitFlowSample.Samples.IntegrationEvents;

// Publisher-owned exchange sample (DeclareExchange, v8.1). This module registers NO consumers:
// the service owns the "integration-events" topic exchange and just publishes to it. External
// clients — other services, other teams — declare their own queues and bind them with whatever
// routing patterns they need, without this service knowing they exist.
//
// That is the standard ownership split in event-driven topologies:
//
//   the producer owns the exchange; each consumer owns its queue and bindings.
//
// The exchange is declared at startup by the library's topology hosted service, so publishing
// never hits a 404 NOT_FOUND even if no subscriber has bound anything yet (messages published
// with no matching binding are simply dropped by the broker — that's topic semantics).
//
// POST /integration-events/subscriptions simulates the EXTERNAL client's side (declare queue +
// bind) so the sample is explorable end-to-end without a second service. In real life that code
// lives in the subscriber's own codebase or provisioning.
public static class IntegrationEventsModule
{
    public const string ExchangeName = "integration-events";

    public static void RegisterTopology(RabbitFlowConfigurator settings)
    {
        // Topic exchange owned by this service. Durable by default; declared at startup with no
        // consumer registration required. Idempotent across restarts, and if the exchange already
        // exists with different settings the library adopts it and logs a warning instead of
        // failing the host start.
        settings.DeclareExchange(ExchangeName, ExchangeType.Topic);
    }

    public static void MapEndpoints(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/integration-events").WithTags("IntegrationEvents");

        // Publish an IntegrationEvent to the owned exchange. The routing key is derived from the
        // payload ("integration.{domain}.{action}"), and the publisher neither knows nor cares
        // which queues — if any — are bound.
        group.MapPost("/", async (
            [FromServices] IRabbitFlowPublisher publisher,
            [FromBody] IntegrationEvent integrationEvent) =>
        {
            var routingKey = $"integration.{integrationEvent.Domain}.{integrationEvent.Action}";

            var result = await publisher.PublishAsync(
                integrationEvent,
                exchangeName: ExchangeName,
                routingKey: routingKey,
                messageId: $"integration-{integrationEvent.EventId}");

            return result.Success
                ? Results.Accepted(value: new { result.MessageId, result.Destination, result.RoutingKey, Payload = integrationEvent })
                : Results.Problem(detail: result.Error?.Message, statusCode: StatusCodes.Status500InternalServerError);
        })
        .WithName("PublishIntegrationEvent")
        .WithSummary("Publishes an IntegrationEvent to the publisher-owned 'integration-events' topic exchange with routing key 'integration.{domain}.{action}'.")
        .Produces(StatusCodes.Status202Accepted);

        // Simulates an EXTERNAL subscriber: declares its own queue and binds it to the exchange
        // with its own pattern. This is deliberately raw RabbitMQ.Client code — it represents what
        // another service (or an operator in the management UI) would do, not something the
        // publishing service is responsible for.
        group.MapPost("/subscriptions", async (
            [FromServices] ConnectionFactory connectionFactory,
            [FromBody] SubscriptionRequest subscription,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(subscription.QueueName) || string.IsNullOrWhiteSpace(subscription.BindingKey))
            {
                return Results.BadRequest("queueName and bindingKey are required.");
            }

            using var connection = await connectionFactory.CreateConnectionAsync("external-subscriber-sim", cancellationToken);

            using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

            await channel.QueueDeclareAsync(subscription.QueueName, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);

            await channel.QueueBindAsync(subscription.QueueName, ExchangeName, subscription.BindingKey, cancellationToken: cancellationToken);

            return Results.Created($"/integration-events/subscriptions/{subscription.QueueName}", new
            {
                subscription.QueueName,
                subscription.BindingKey,
                Exchange = ExchangeName
            });
        })
        .WithName("SimulateExternalSubscription")
        .WithSummary("Simulates an external client: declares its own queue and binds it to 'integration-events' with the given pattern (e.g. 'integration.user.*' or 'integration.#').")
        .Produces(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest);

        // Peek at a simulated subscriber's queue to verify routing — message count grows as
        // matching events are published.
        group.MapGet("/subscriptions/{queueName}", async (
            [FromServices] IRabbitFlowState state,
            [FromRoute] string queueName,
            CancellationToken cancellationToken) =>
        {
            var queueState = await state.GetQueueStateAsync(queueName, cancellationToken);

            return queueState.Exists ? Results.Ok(queueState) : Results.NotFound();
        })
        .WithName("GetSubscriptionQueueState")
        .WithSummary("Returns the state (message count, consumers) of a simulated subscriber queue.")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);
    }

    public sealed record SubscriptionRequest(string QueueName, string BindingKey);
}
