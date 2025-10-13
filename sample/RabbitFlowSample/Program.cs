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
    });

    settings.ConfigurePublisher(publisherSettings => publisherSettings.DisposePublisherConnection = false); // OPTIONAL


    settings.AddConsumer<EmailConsumer>(queueName: "email-queue", consumerSettings =>
    {
        consumerSettings.Enable = true;

        consumerSettings.ConsumerId = "EmailQueueConsumer";

        consumerSettings.Timeout = TimeSpan.FromMilliseconds(2000);
        
        consumerSettings.AutoGenerate = true;

        consumerSettings.ConfigureAutoGenerate(opt =>
        {
            opt.ExchangeName = "notifications";
            opt.ExchangeType = ExchangeType.Fanout;
            opt.ExclusiveQueue = false;
        });

        consumerSettings.ConfigureRetryPolicy(retryPolicy =>
        {
            retryPolicy.MaxRetryCount = 3;
            retryPolicy.RetryInterval = 1000;
            retryPolicy.ExponentialBackoff = true;
            retryPolicy.ExponentialBackoffFactor = 2;
        });

        consumerSettings.ExtendDeadletterMessage = true;

    });

    settings.AddConsumer<WhatsAppConsumer>(queueName: "whatsapp-queue", consumerSettings =>
    {
        consumerSettings.Timeout = TimeSpan.FromMilliseconds(3000);
        
        consumerSettings.AutoGenerate = true;

        consumerSettings.ConfigureAutoGenerate(opt =>
        {
            opt.ExchangeName = "notifications";
            opt.ExchangeType = ExchangeType.Fanout;
            opt.ExclusiveQueue = false;
        });

        consumerSettings.ExtendDeadletterMessage = true;     
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
    await publisher.PublishAsync(eventPayload, exchangeName: "notifications", routingKey: "");
    
    return Results.Accepted(value: eventPayload);
})
.WithName("PublishNotificationFanout")
.WithSummary("Publishes NotificationEvent to fanout exchange 'notifications'.")
.Produces(StatusCodes.Status202Accepted);

// Direct email send bypassing fanout (goes only to email-send-queue)
app.MapPost("/email", async ([FromServices] IRabbitFlowPublisher publisher, [FromBody] NotificationEvent eventPayload) =>
{
    await publisher.PublishAsync(eventPayload, "email-queue");
    
    return Results.Accepted(value: eventPayload);
})
.WithName("PublishEmailDirectQueue")
.WithSummary("Publishes NotificationEvent directly to email send queue.")
.Produces(StatusCodes.Status202Accepted);

// Direct whatsapp delivery bypassing fanout
app.MapPost("/whatsapp", async ([FromServices] IRabbitFlowPublisher publisher, [FromBody] NotificationEvent eventPayload) =>
{
    await publisher.PublishAsync(eventPayload, "whatsapp-queue");
    
    return Results.Accepted(value: eventPayload);
})
.WithName("PublishWhatsAppDirectQueue")
.WithSummary("Publishes NotificationEvent directly to whatsapp delivery queue.")
.Produces(StatusCodes.Status202Accepted);


app.MapPost("/volatile", async (int count, [FromServices] IRabbitFlowTemporary rabbitFlowTemporary, ILogger<Program> logger,  CancellationToken ct) =>
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


app.MapPost("/volatile-fire-and-forget", async (int count, [FromServices] IRabbitFlowTemporary rabbitFlowTemporary, ILogger<Program> logger,  CancellationToken ct = default) =>
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
