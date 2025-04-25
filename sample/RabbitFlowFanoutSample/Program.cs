using EasyRabbitFlow;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using RabbitFlowFanoutSample.Consumers;
using RabbitFlowFanoutSample.Events;

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

        consumerSettings.Timeout = TimeSpan.FromMilliseconds(500);

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

        consumerSettings.AutoGenerate = true;

        consumerSettings.ConfigureAutoGenerate(opt =>
        {
            opt.ExchangeName = "notifications";
            opt.ExchangeType = ExchangeType.Fanout;
            opt.ExclusiveQueue = false;
        });
    });

});

var app = builder.Build();

await app.Services.InitializeConsumerAsync<NotificationEvent, EmailConsumer>();

await app.Services.InitializeConsumerAsync<NotificationEvent, WhatsAppConsumer>(opt =>
{
    opt.CreateNewInstancePerMessage = true; // A new scope of services is created. Required if you are using Scoped or Transcient services.
    opt.Active = true; // if you want to disable this consumer
});

app.UseHttpsRedirection();

app.MapPost("/notification", async (IRabbitFlowPublisher publisher, NotificationEvent emailEvent) =>
{
    await publisher.PublishAsync(emailEvent, exchangeName: "notifications", routingKey: "");
});

app.MapPost("/volatile", (IRabbitFlowTemporary rabbitFlowTemporary, ILogger<Program> logger, VolatileEvent emailEvent, CancellationToken cancellationToken) =>
{
    var events = Enumerable.Range(0, 20).Select(i => new VolatileEvent
    {
        Id = Guid.NewGuid()

    }).ToArray();

    var events2 = Enumerable.Range(0, 5).Select(i => new VolatileEvent
    {
        Id = Guid.NewGuid()

    }).ToArray();


    rabbitFlowTemporary.RunAsync(events, onMessageReceived: async (@event, ct) =>
         {
             logger.LogWarning("[{Timestamp}] - P1 Procesando: {@e}", DateTime.Now, @event.Id);

             await Task.Delay((int)TimeSpan.FromSeconds(20).TotalMilliseconds, ct);

             logger.LogWarning("[{Timestamp}] - P1 Completado: {@e}", DateTime.Now, @event.Id);

         }, onCompleted: (totalProcessed, errors) =>
            {
                logger.LogWarning("[{Timestamp}] - P1 Se han completado: {processed}", DateTime.Now, totalProcessed);

            }, new RunTemporaryOptions
            {
                PrefetchCount = 1,
                Timeout = TimeSpan.FromSeconds(30),
                QueuePrefixName = "volatile",
            }, cancellationToken: CancellationToken.None)
    .ContinueWith(t => { }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    rabbitFlowTemporary.RunAsync(events2, onMessageReceived: async (@event, ct) =>
    {
        logger.LogWarning("[{Timestamp}] - P2 Procesando: {@e}", DateTime.Now, @event.Id);

        await Task.Delay((int)TimeSpan.FromSeconds(20).TotalMilliseconds, ct);

        logger.LogWarning("[{Timestamp}] - P2 Completado: {@e}", DateTime.Now, @event.Id);

    }, onCompleted: (totalProcessed, errors) =>
    {
        logger.LogWarning("[{Timestamp}] - P2 Se han completado: {processed}", DateTime.Now, totalProcessed);

    }, new RunTemporaryOptions
    {
        PrefetchCount = 1,
        Timeout = TimeSpan.FromSeconds(30),
        QueuePrefixName = "volatile",
    }, cancellationToken: CancellationToken.None)
.ContinueWith(t => { }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

});

app.Run();