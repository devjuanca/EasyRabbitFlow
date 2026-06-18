using EasyRabbitFlow;
using EasyRabbitFlow.Services;
using Microsoft.OpenApi;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
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

// OpenTelemetry: EasyRabbitFlow publishes spans through an ActivitySource and metrics through a Meter,
// both named "EasyRabbitFlow". Registering them here makes publish/process traces and message counters
// visible in the Aspire dashboard (or any OTLP collector).
var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("rabbitflow-sample"))
    .WithTracing(tracing => tracing
        .AddSource(RabbitFlowDiagnostics.ActivitySourceName)
        .AddAspNetCoreInstrumentation())
    .WithMetrics(metrics => metrics
        .AddMeter(RabbitFlowDiagnostics.MeterName)
        .AddAspNetCoreInstrumentation());

// The Aspire AppHost injects OTEL_EXPORTER_OTLP_ENDPOINT automatically. When running standalone
// without a collector, skip the exporter so nothing retries against a dead endpoint.
if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    otel.UseOtlpExporter();
}

builder.Services.AddRabbitFlow(settings =>
{
    settings.ConfigureHost(host =>
    {
        // When orchestrated by Aspire, the AppHost injects ConnectionStrings__rabbitmq
        // as an AMQP URI (amqp://user:pass@host:port). Fall back to localhost for standalone runs.
        var connectionString = builder.Configuration.GetConnectionString("rabbitmq");

        if (!string.IsNullOrEmpty(connectionString))
        {
            var amqp = new Uri(connectionString);

            var userInfo = amqp.UserInfo.Split(':');

            host.Host = amqp.Host;
            host.Port = amqp.Port > 0 ? amqp.Port : 5672;
            host.Username = userInfo.Length > 0 && userInfo[0].Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : "guest";
            host.Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "guest";
        }
        else
        {
            host.Host = "localhost";
            host.Username = "guest";
            host.Password = "guest";
            host.Port = 5672;
        }

        host.AutomaticRecoveryEnabled = true;
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

// Native health check: broker connectivity + a few key queues. The queues are declared by the
// consumers at startup, and since this app hosts them, RequireConsumers should hold true.
builder.Services.AddHealthChecks()
    .AddRabbitFlow(configure: options =>
    {
        options.Queues.Add(OrdersModule.CreatedQueue);
        options.Queues.Add(PaymentsModule.MainQueue);
        options.Queues.Add(SupportTicketsModule.MainQueue);
        options.RequireConsumers = true;
    });

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

app.MapHealthChecks("/health");

// Unified queue-state snapshot: every sample queue in a single broker connection.
app.MapGet("/diagnostics/queues", async (IRabbitFlowState state, CancellationToken cancellationToken) =>
{
    var queues = new[]
    {
        NotificationsModule.EmailQueue,
        NotificationsModule.WhatsAppQueue,
        OrdersModule.EuQueue,
        OrdersModule.CreatedQueue,
        OrdersModule.AuditQueue,
        PaymentsModule.MainQueue,
        SupportTicketsModule.MainQueue
    };

    var states = await state.GetQueuesStateAsync(queues, cancellationToken);

    return Results.Ok(states);
})
.WithName("GetQueuesState")
.WithTags("Diagnostics");

NotificationsModule.MapEndpoints(app);

OrdersModule.MapEndpoints(app);

PaymentsModule.MapEndpoints(app);

SupportTicketsModule.MapEndpoints(app);

ThumbnailsModule.MapEndpoints(app);

app.Run();
