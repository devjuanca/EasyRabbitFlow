using EasyRabbitFlow;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using RabbitFlowSample;
using RabbitFlowSample.Consumers;
using RabbitFlowSample.Events;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRabbitFlow(settings =>
{
    settings.ConfigureHost(hostSettings =>
    {
        hostSettings.Host = "localhost";
        hostSettings.Username = "guest";
        hostSettings.Password = "guest";
        hostSettings.Port = 5672; // Default Port. OPTIONAL
    });

    settings.ConfigurePublisher(publisherSettings => publisherSettings.DisposePublisherConnection = false); // OPTIONAL

    settings.AddConsumer<EmailConsumer>(queueName: "emails-test-queue", consumerSettings =>
    {
        consumerSettings.ConsumerId = "Email";

        consumerSettings.PrefetchCount = 5;

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

    });

    settings.AddConsumer<WhatsAppConsumer>("whatsapps-test-queue", consumerSettings =>
    {
        consumerSettings.ConsumerId = "WhatsApp";

        consumerSettings.PrefetchCount = 5;

        consumerSettings.AutoGenerate = true;

        consumerSettings.ConfigureAutoGenerate(opt =>
        {
            opt.ExchangeName = "notifications";
            opt.ExchangeType = ExchangeType.Fanout;
            opt.ExclusiveQueue = false;
        });
    });

    settings.AddConsumer<ServiceLifetimeTestConsumer>("service-lifetime-test", consumerSettings =>
    {
        consumerSettings.AutoGenerate = true;

        consumerSettings.PrefetchCount = 5;

        consumerSettings.ExtendDeadletterMessage = true;

        consumerSettings.Timeout = TimeSpan.FromSeconds(10);

        consumerSettings.ConfigureRetryPolicy(retryPolicy =>
        {
            retryPolicy.MaxRetryCount = 2;
        });
    });

});

builder.Services.AddSingleton<GuidSingletonService>();

builder.Services.AddScoped<GuidScopedService>();

builder.Services.AddTransient<GuidTransientService>();

var app = builder.Build();

await app.Services.InitializeConsumerAsync<NotificationEvent, EmailConsumer>();

await app.Services.InitializeConsumerAsync<NotificationEvent, WhatsAppConsumer>();

await app.Services.InitializeConsumerAsync<ServiceLifetimeEvent, ServiceLifetimeTestConsumer>();

app.UseHttpsRedirection();

app.MapPost("/notification", async (IRabbitFlowPublisher publisher, NotificationEvent emailEvent) =>
{
    await Task.WhenAll(Enumerable.Range(0, 1).Select(i => publisher.PublishAsync(emailEvent, exchangeName: "notifications", routingKey: "")));

});

app.MapPost("/whatsapp", async (IRabbitFlowPublisher publisher, NotificationEvent emailEvent) =>
{
    await  publisher.PublishAsync(emailEvent, "whatsapps-test-queue");
});

app.MapPost("/email", async (IRabbitFlowPublisher publisher, NotificationEvent emailEvent) =>
{
    await publisher.PublishAsync(emailEvent, "emails-test-queue");
});

app.MapPost("/service-lifetime-test", async (IRabbitFlowPublisher publisher, ILogger<Program> logger) =>
{
    await Task.WhenAll(Enumerable.Range(0, 20).Select(i => publisher.PublishAsync(new ServiceLifetimeEvent(), "service-lifetime-test")));
});

app.MapPost("/volatile", (IRabbitFlowTemporary rabbitFlowTemporary, ILogger<Program> logger, VolatileEvent emailEvent, CancellationToken cancellationToken) =>
{
    var events = Enumerable.Range(0, 15).Select(i => new VolatileEvent
    {
        Id = Guid.NewGuid()

    }).ToArray();


    rabbitFlowTemporary.RunAsync(
        messages: events,
        onMessageReceived: async (@event, ct) =>
         {
             await Task.Delay(TimeSpan.FromMilliseconds(1000), ct);

             logger.LogWarning("[{Timestamp}] - P1 Completado: {@e}", DateTime.Now, @event.Id);

         },
        onCompleted: (totalProcessed, errors) =>
        {
            logger.LogWarning("[{Timestamp}] - COMPLETED: {processed}", DateTime.Now, totalProcessed);
        },
       options: new RunTemporaryOptions
       {
           PrefetchCount = 5,
           Timeout = TimeSpan.FromSeconds(30),
           QueuePrefixName = "volatile",
       },
       cancellationToken: CancellationToken.None)
    .ContinueWith(t => { }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

});


app.MapPost("/volatile-with-results", (IRabbitFlowTemporary rabbitFlowTemporary, ILogger<Program> logger, IServiceScopeFactory serviceScopeFactory, VolatileEvent emailEvent, CancellationToken cancellationToken) =>
{
    var events = Enumerable.Range(0, 20).Select(i => new VolatileEvent
    {
        Id = Guid.NewGuid()
    }).ToArray();

    rabbitFlowTemporary.RunAsync(
    messages: events,
    onMessageReceived: async (@event, ct) =>
    {

        await Task.Delay(TimeSpan.FromMilliseconds(1000), cancellationToken);

        using var scope = serviceScopeFactory.CreateScope();

        var guidService = scope.ServiceProvider.GetRequiredService<GuidSingletonService>();

        int shouldFail = new Random().Next(0, 10);

        if (shouldFail < 5)
        {
            throw new Exception("Simulated failure for testing purposes.");
        }

        logger.LogWarning("[{Timestamp}] - P2 Completado: {@e}", DateTime.Now, @event.Id);

        return new VolatileResult(@event.Id, guidService.Guid, true, DateTime.UtcNow);

    },
    onCompletedAsync: async (totalMessages, results) =>
    {
        logger.LogWarning("Completed: {count} from a total of {totalMessages}", results.Count, totalMessages);

        foreach (var result in results)
        {
            logger.LogWarning("[{Timestamp}] - Resultado: {@result}", result.Timestamp, result);
        }

        await Task.CompletedTask;
    },
    options: new RunTemporaryOptions
    {
        PrefetchCount = 5,
        Timeout = TimeSpan.FromSeconds(30),
        QueuePrefixName = "volatile-with-results",
    },
    cancellationToken: CancellationToken.None)
.ContinueWith(t => { }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

});


app.Run();

internal record VolatileResult(Guid EventId, Guid CompletionId, bool Success, DateTime Timestamp);