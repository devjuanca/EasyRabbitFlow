using EasyRabbitFlow;
using Microsoft.OpenApi;
using RabbitFlowSample.Common;
using RabbitFlowSample.Samples.Notifications;
using RabbitFlowSample.Samples.Orders;
using RabbitFlowSample.Samples.Payments;
using RabbitFlowSample.Samples.SupportTickets;
using RabbitFlowSample.Samples.Thumbnails;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "EasyRabbitFlow Sample API",
        Version = "v1",
        Description =
            "Demos for the EasyRabbitFlow library. Each tag corresponds to a self-contained sample under /Samples; " +
            "see the README in each sample folder for a description of the pattern and its topology."
    });
});


builder.Services.AddRabbitFlow(settings =>
{
    settings.ConfigureHost(host =>
    {
        host.Host = "localhost";
        host.Username = "guest";
        host.Password = "guest";
        host.Port = 5672;
        host.AutomaticRecoveryEnabled = true;
        host.TopologyRecoveryEnabled = true;
    });

    settings.ConfigurePublisher(pub =>
    {
        pub.DisposePublisherConnection = false;
        pub.PublisherId = "sample-api";
    });

    NotificationsModule.RegisterConsumers(settings);
    
    OrdersModule.RegisterConsumers(settings);
    
    PaymentsModule.RegisterConsumers(settings);

    SupportTicketsModule.RegisterConsumers(settings);

    ThumbnailsModule.RegisterConsumers(settings);

}).UseRabbitFlowConsumers();

// Demo DI services consumed by WhatsAppConsumer (see Samples/Notifications/WhatsAppConsumer.cs).
builder.Services.AddSingleton<GuidSingletonService>();

builder.Services.AddScoped<GuidScopedService>();

builder.Services.AddTransient<GuidTransientService>();

var app = builder.Build();

app.UseSwagger();

app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/swagger/v1/swagger.json", "EasyRabbitFlow Sample API v1");
    o.DocumentTitle = "EasyRabbitFlow Sample API";
});

app.UseHttpsRedirection();

NotificationsModule.MapEndpoints(app);

OrdersModule.MapEndpoints(app);

PaymentsModule.MapEndpoints(app);

SupportTicketsModule.MapEndpoints(app);

ThumbnailsModule.MapEndpoints(app);

app.Run();
