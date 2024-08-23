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
        consumerSettings.PrefetchCount = 5;

        consumerSettings.Timeout = TimeSpan.FromMilliseconds(500);

        consumerSettings.AutoGenerate = true;

        consumerSettings.ConfigureAutoGenerate(opt =>
        {
            opt.DurableQueue = true;
            opt.DurableExchange = true;
            opt.ExclusiveQueue = false;
            opt.AutoDeleteQueue = false;
            opt.GenerateDeadletterQueue = true;
            opt.ExchangeType = ExchangeType.Direct;
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
        consumerSettings.AutoGenerate = true;

        consumerSettings.ConfigureAutoGenerate(opt =>
        {
            opt.DurableQueue = true;
            opt.DurableExchange = true;
            opt.ExclusiveQueue = false;
            opt.AutoDeleteQueue = false;
            opt.GenerateDeadletterQueue = true;
            opt.ExchangeType = ExchangeType.Direct;
        });
    });

});

var app = builder.Build();

app.UseConsumer<EmailEvent, EmailConsumer>();

app.UseConsumer<WhatsAppEvent, WhatsAppConsumer>(opt =>
{
    opt.PerMessageInstance = true; // A new scope of services is created. Required if you are using Scoped or Transcient services.
    opt.Active = true; // if you want to disable this consumer
});

app.UseHttpsRedirection();

app.MapPost("/email", async (IRabbitFlowPublisher publisher, EmailEvent emailEvent) =>
{
    await publisher.PublishAsync(emailEvent, queueName: "emails-test-queue");
});

app.MapPost("/whatsapp", async (IRabbitFlowPublisher publisher, WhatsAppEvent whatsAppEvent) =>
{
    await publisher.PublishAsync(whatsAppEvent, queueName: "whatsapps-test-queue");
});

app.Run();