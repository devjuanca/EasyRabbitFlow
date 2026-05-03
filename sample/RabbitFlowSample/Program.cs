using EasyRabbitFlow;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using Microsoft.AspNetCore.Mvc;
using RabbitFlowSample;
using RabbitFlowSample.Consumers;
using RabbitFlowSample.Events;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen();

builder.Services.AddRabbitFlow(settings =>
{
    settings.ConfigureHost(hostSettings =>
    {
        hostSettings.Host = "localhost";
        hostSettings.Username = "guest";
        hostSettings.Password = "guest";
        hostSettings.Port = 5672; // Optional (default 5672)
        hostSettings.AutomaticRecoveryEnabled = true; // Optional (default true)
        hostSettings.TopologyRecoveryEnabled = true; // Optional (default true)
    });

    settings.ConfigurePublisher(publisherSettings =>
    {
        publisherSettings.DisposePublisherConnection = false;
    }
    ); // OPTIONAL


    settings.AddConsumer<EmailConsumer>(queueName: "email-queue", consumerSettings =>
    {
        consumerSettings.Enable = true;

        consumerSettings.ConsumerId = "EmailQueueConsumer";

        consumerSettings.Timeout = TimeSpan.FromMilliseconds(1890000);

        consumerSettings.AutoGenerate = true;

        consumerSettings.ConfigureAutoGenerate(opt =>
        {
            opt.ExchangeName = "notifications";
            opt.ExchangeType = ExchangeType.Fanout;
            opt.ExclusiveQueue = false;
        });

        consumerSettings.ConfigureRetryPolicy(retryPolicy =>
        {
            retryPolicy.MaxRetryCount = 1;
            retryPolicy.RetryInterval = 1000;
            retryPolicy.ExponentialBackoff = true;
            retryPolicy.ExponentialBackoffFactor = 2;
        });

        consumerSettings.ExtendDeadletterMessage = true;

    });

    settings.AddConsumer<WhatsAppConsumer>(queueName: "whatsapp-queue", consumerSettings =>
    {
        consumerSettings.Timeout = TimeSpan.FromSeconds(60);

        consumerSettings.AutoGenerate = true;

        consumerSettings.ConfigureAutoGenerate(opt =>
        {
            opt.ExchangeName = "notifications";
            opt.ExchangeType = ExchangeType.Fanout;
            opt.ExclusiveQueue = false;
        });

        consumerSettings.ExtendDeadletterMessage = true;

        consumerSettings.ConfigureDeadLetterReprocess(opt =>
        {
            opt.Enabled = true;
            opt.MaxReprocessAttempts = 2;
            opt.Interval = TimeSpan.FromHours(1);
        });
    });

    // ─── Topic exchange demo ───────────────────────────────────────────────
    // Three consumers share the same Topic exchange "orders-topic" but bind
    // to different patterns. The producer publishes with concrete routing
    // keys shaped "orders.{region}.{status}" (e.g. "orders.eu.created");
    // each consumer receives only the messages whose routing key matches
    // its binding, courtesy of the Topic wildcards * (one word) and # (any words).

    settings.AddConsumer<EuOrdersConsumer>(queueName: "orders-eu-queue", c =>
    {
        c.AutoGenerate = true;
        c.ConfigureAutoGenerate(opt =>
        {
            opt.ExchangeName = "orders-topic";
            opt.ExchangeType = ExchangeType.Topic;
            opt.RoutingKey = "orders.eu.*";        // every status, EU only
        });
    });

    settings.AddConsumer<OrderCreatedConsumer>(queueName: "orders-created-queue", c =>
    {
        c.AutoGenerate = true;
        c.ConfigureAutoGenerate(opt =>
        {
            opt.ExchangeName = "orders-topic";
            opt.ExchangeType = ExchangeType.Topic;
            opt.RoutingKey = "orders.*.created";   // "created" status, any region
        });
    });

    settings.AddConsumer<OrderAuditConsumer>(queueName: "orders-audit-queue", c =>
    {
        c.AutoGenerate = true;
        c.ConfigureAutoGenerate(opt =>
        {
            opt.ExchangeName = "orders-topic";
            opt.ExchangeType = ExchangeType.Topic;
            opt.RoutingKey = "orders.#";           // catch-all (audit log)
        });
    });

}).UseRabbitFlowConsumers();

builder.Services.AddSingleton<GuidSingletonService>();

builder.Services.AddScoped<GuidScopedService>();

builder.Services.AddTransient<GuidTransientService>();

var app = builder.Build();

app.UseSwagger();

app.UseSwaggerUI();

app.UseHttpsRedirection();


// Fanout broadcast (both email & whatsapp consumers receive)
app.MapPost("/notification", async ([FromServices] IRabbitFlowPublisher publisher, [FromBody] NotificationEvent eventPayload) =>
{
    var result = await publisher.PublishAsync(eventPayload, exchangeName: "notifications", routingKey: "", messageId: $"notification-{Guid.NewGuid()}");

    return result.Success
        ? Results.Accepted(value: new { result.MessageId, result.Destination, result.TimestampUtc, Payload = eventPayload })
        : Results.Problem(detail: result.Error?.Message, statusCode: StatusCodes.Status500InternalServerError);
})
.WithName("PublishNotificationFanout")
.WithSummary("Publishes NotificationEvent to fanout exchange 'notifications'.")
.Produces(StatusCodes.Status202Accepted);

// Direct email send bypassing fanout (goes only to email-send-queue)
app.MapPost("/email", async ([FromServices] IRabbitFlowPublisher publisher, [FromBody] NotificationEvent eventPayload) =>
{
    var result = await publisher.PublishAsync(eventPayload, "email-queue", correlationId: Guid.NewGuid().ToString());

    return result.Success
        ? Results.Accepted(value: new { result.MessageId, result.Destination, result.TimestampUtc, Payload = eventPayload })
        : Results.Problem(detail: result.Error?.Message, statusCode: StatusCodes.Status500InternalServerError);
})
.WithName("PublishEmailDirectQueue")
.WithSummary("Publishes NotificationEvent directly to email send queue.")
.Produces(StatusCodes.Status202Accepted);

// Direct whatsapp delivery bypassing fanout
app.MapPost("/whatsapp", async ([FromServices] IRabbitFlowPublisher publisher, [FromBody] NotificationEvent eventPayload) =>
{
    var result = await publisher.PublishAsync(eventPayload, "whatsapp-queue");

    return result.Success
        ? Results.Accepted(value: new { result.MessageId, result.Destination, result.TimestampUtc, Payload = eventPayload })
        : Results.Problem(detail: result.Error?.Message, statusCode: StatusCodes.Status500InternalServerError);
})
.WithName("PublishWhatsAppDirectQueue")
.WithSummary("Publishes NotificationEvent directly to whatsapp delivery queue.")
.Produces(StatusCodes.Status202Accepted);

// Batch publish to fanout exchange (atomic by default - Transactional)
app.MapPost("/notification/batch", async ([FromServices] IRabbitFlowPublisher publisher, [FromBody] NotificationEvent[] events) =>
{
    var result = await publisher.PublishBatchAsync(events, exchangeName: "notifications", routingKey: "", messageIdSelector: e => $"notification-{Guid.NewGuid()}");

    return result.Success
        ? Results.Accepted(value: new { result.MessageCount, result.MessageIds, result.Destination, result.ChannelMode, result.TimestampUtc })
        : Results.Problem(detail: result.Error?.Message, statusCode: StatusCodes.Status500InternalServerError);
})
.WithName("PublishNotificationBatch")
.WithSummary("Publishes a batch of NotificationEvents atomically (Transactional by default).")
.Produces(StatusCodes.Status202Accepted);

// Topic exchange — publish one OrderEvent. The path segments build the routing key
// "orders.{region}.{status}", and the broker dispatches it to whichever consumers'
// bindings match. Try the following from Swagger to see the patterns at work:
//   POST /orders/eu/created   → audit + EU + created
//   POST /orders/us/created   → audit + created   (NOT eu)
//   POST /orders/eu/shipped   → audit + EU        (NOT created)
//   POST /orders/ap/cancelled → audit only        (no EU, no created)
app.MapPost("/orders/{region}/{status}", async (
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
        exchangeName: "orders-topic",
        routingKey: routingKey,
        messageId: $"order-{payload.OrderId}-{status.ToLowerInvariant()}");

    return result.Success
        ? Results.Accepted(value: new { result.MessageId, result.Destination, RoutingKey = routingKey, Payload = payload })
        : Results.Problem(detail: result.Error?.Message, statusCode: StatusCodes.Status500InternalServerError);
})
.WithName("PublishOrderEventTopic")
.WithSummary("Publishes an OrderEvent to topic exchange 'orders-topic' with routing key 'orders.{region}.{status}'.")
.Produces(StatusCodes.Status202Accepted);

// Topic exchange — bulk publish a random mix of regions/statuses. Useful to watch
// the three consumers light up at different rates depending on their binding.
app.MapPost("/orders/random/{count:int}", async (
    int count,
    [FromServices] IRabbitFlowPublisher publisher) =>
{
    var regions = new[] { "eu", "us", "ap" };
    var statuses = new[] { "created", "shipped", "cancelled" };
    var rng = Random.Shared;
    var produced = new List<object>(count);

    for (var i = 0; i < count; i++)
    {
        var region = regions[rng.Next(regions.Length)];
        var status = statuses[rng.Next(statuses.Length)];

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
            exchangeName: "orders-topic",
            routingKey: routingKey,
            messageId: $"order-{order.OrderId}-{status}");

        produced.Add(new { result.MessageId, RoutingKey = routingKey, order.OrderId, order.Amount });
    }

    return Results.Accepted(value: new { count = produced.Count, items = produced });
})
.WithName("PublishOrderEventTopicRandom")
.WithSummary("Publishes a random burst of OrderEvents across regions and statuses to 'orders-topic'.")
.Produces(StatusCodes.Status202Accepted);


app.MapPost("/volatile", async (int count, [FromServices] IRabbitFlowTemporary rabbitFlowTemporary, ILogger<Program> logger, CancellationToken ct) =>
{
    var events = Enumerable.Range(0, count).Select(_ => new VolatileEvent()).ToArray();

    var processed = await rabbitFlowTemporary.RunAsync(
        messages: events,
        onMessageReceived: async (@event, msgCt) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), msgCt);
          
            logger.LogInformation("Processed volatile event {Id}", @event.Id);
        },
        onCompleted: (totalProcessed, errors) =>
        {
            logger.LogInformation("Volatile batch completed. Total={Total}, Errors={Errors}", totalProcessed, errors);
        },
        onError: async (@event, errorCt) =>
        {
            logger.LogWarning("Failed to process volatile event {Id}", @event.Id);
        },
        options: new RunTemporaryOptions
        {
            PrefetchCount = 2,
            Timeout = TimeSpan.FromSeconds(10),
            QueuePrefixName = "volatile",
        },
        cancellationToken: ct);

    return Results.Ok(new { requested = count, processed });
})
.WithName("RunVolatileAndForget")
.WithSummary("Runs a temporary volatile queue.")
.Produces(StatusCodes.Status200OK);


app.MapPost("/volatile-fire-and-forget", async (int count, [FromServices] IRabbitFlowTemporary rabbitFlowTemporary, ILogger<Program> logger, CancellationToken ct = default) =>
{
    var events = Enumerable.Range(0, count).Select(_ => new VolatileEvent()).ToArray();

    rabbitFlowTemporary.RunAsync(
        messages: events,
        onMessageReceived: async (@event, ct) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);

            logger.LogInformation("Processed volatile event {Id}", @event.Id);
        },
        onCompleted: (totalProcessed, errors) =>
        {
            logger.LogInformation("Volatile batch completed. Total={Total}, Errors={Errors}", totalProcessed, errors);
        },
        onError: (@event, errorCt) =>
        {
            logger.LogWarning("Failed to process volatile event {Id}", @event.Id);

            return Task.CompletedTask;
        },
        options: new RunTemporaryOptions
        {
            PrefetchCount = 2,
            Timeout = TimeSpan.FromSeconds(10),
            QueuePrefixName = "volatile",
        },
        cancellationToken: CancellationToken.None)
    .FireAndForget(cancellationToken: CancellationToken.None);

    return Results.Accepted();
})
.WithName("RunVolatile")
.WithSummary("Runs a temporary volatile queue as a fire and forget.")
.Produces(StatusCodes.Status202Accepted);


app.Run();
