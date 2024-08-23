using RabbitFlow.Configuration;
using RabbitFlow.Services;
using RabbitFlow.Settings;
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

app.UseConsumer<NotificationEvent, EmailConsumer>();

app.UseConsumer<NotificationEvent, WhatsAppConsumer>(opt =>
{
    opt.PerMessageInstance = true; // A new scope of services is created. Required if you are using Scoped or Transcient services.
    opt.Active = true; // if you want to disable this consumer
});

app.UseHttpsRedirection();

app.MapPost("/notification", async (IRabbitFlowPublisher publisher, NotificationEvent emailEvent) =>
{
    await publisher.PublishAsync(emailEvent, exchangeName: "notifications", routingKey: "");
});


app.Run();