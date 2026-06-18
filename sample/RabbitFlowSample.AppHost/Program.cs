var builder = DistributedApplication.CreateBuilder(args);

// Fixed credentials so the broker matches the sample's standalone defaults (guest/guest)
// and the management UI is predictable across restarts.
var username = builder.AddParameter("rabbitmq-username", value: "guest");

var password = builder.AddParameter("rabbitmq-password", value: "guest", secret: true);

var rabbitmq = builder.AddRabbitMQ("rabbitmq", username, password)
    .WithManagementPlugin();

// The sample API receives ConnectionStrings__rabbitmq (amqp://guest:guest@host:port) and the
// OTLP endpoint of the Aspire dashboard, where EasyRabbitFlow traces and metrics show up.
builder.AddProject<Projects.RabbitFlowSample>("rabbitflow-sample")
    .WithExternalHttpEndpoints()
    .WithReference(rabbitmq)
    .WaitFor(rabbitmq);

builder.Build().Run();
