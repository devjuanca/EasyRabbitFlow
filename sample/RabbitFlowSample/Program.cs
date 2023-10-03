using RabbitFlow.Configuration;
using RabbitFlow.Services;
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

    settings.ConfigurePublisher(publisherSettings => publisherSettings.DisposePublisherConnection = true); // OPTIONAL

    settings.AddConsumer(queueName: "email-queue", consumerSettings =>
    {
        consumerSettings.AutoAck = true;
        consumerSettings.PrefetchCount = 5;
        consumerSettings.Timeout = TimeSpan.FromMilliseconds(500);
        consumerSettings.ConfigureRetryPolicy<EmailConsumer>(retryPolicy =>
        {
            retryPolicy.MaxRetryCount = 3;
            retryPolicy.RetryInterval = 1000;
            retryPolicy.ExponentialBackoff = true;
            retryPolicy.ExponentialBackoffFactor = 2;
        });

        consumerSettings.SetConsumerHandler<EmailConsumer>();
    });

    settings.AddConsumer("whatsapp-queue", consumerSettings => consumerSettings.SetConsumerHandler<WhatsAppConsumer>());

});

var app = builder.Build();

app.UseConsumer<EmailEvent, EmailConsumer>();

app.UseConsumer<WhatsAppEvent, WhatsAppConsumer>(opt =>
{
    opt.PerMessageInstance = true;
    opt.Active = true; // if you want to disable this consumer
});

app.UseHttpsRedirection();

app.MapPost("/email", async (IRabbitFlowPublisher publisher, EmailEvent emailEvent) =>
{
    await publisher.PublishAsync(emailEvent, exchangeName: "notifications", routingKey: "email");
});

app.MapPost("/whatsapp", async (IRabbitFlowPublisher publisher, WhatsAppEvent whatsAppEvent) =>
{
    await publisher.PublishAsync(whatsAppEvent, exchangeName: "notifications", routingKey: "whatsapp");
});

app.Run();

