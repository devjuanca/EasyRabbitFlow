using EasyRabbitFlow;
using EasyRabbitFlow.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace EasyRabbitFlow.Tests.Fixtures;

public class RabbitMqFixture : IAsyncLifetime
{
    // RabbitMQ 4.x (matches what Aspire provisions). 4.x is stricter than 3.13: notably it rejects the
    // 'x-consumer-timeout' queue argument on queue.declare, which the consumer must tolerate.
    // The image tag can be overridden via the RABBITMQ_IMAGE env var so CI can run the suite against
    // several broker versions (see the matrix in .github/workflows/ci.yml); defaults to 4.3-management.
    private readonly RabbitMqContainer _container = new RabbitMqBuilder(ResolveImage())
        .WithUsername("guest")
        .WithPassword("guest")
        .Build();

    private static string ResolveImage()
    {
        var image = Environment.GetEnvironmentVariable("RABBITMQ_IMAGE");
        return string.IsNullOrWhiteSpace(image) ? "rabbitmq:4.3-management" : image;
    }

    public string Host => _container.Hostname;
    public int Port => _container.GetMappedPublicPort(5672);
    public string Username => "guest";
    public string Password => "guest";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    /// <summary>
    /// Builds a service provider with EasyRabbitFlow configured against the Testcontainer.
    /// </summary>
    public IServiceProvider BuildServiceProvider(Action<RabbitFlowConfigurator>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddRabbitFlow(settings =>
        {
            settings.ConfigureHost(h =>
            {
                h.Host = Host;
                h.Port = Port;
                h.Username = Username;
                h.Password = Password;
            });

            configure?.Invoke(settings);
        });

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Builds a service provider with consumer hosted service registered.
    /// </summary>
    public IServiceProvider BuildServiceProviderWithConsumers(Action<RabbitFlowConfigurator> configure)
    {
        var services = new ServiceCollection();

        // Capture library logs into a static buffer so timing-sensitive tests (e.g. recovery) can surface what
        // the library actually did on failure. See MemoryLogSink.
        services.AddLogging(b => b.AddProvider(new EasyRabbitFlow.Tests.Helpers.MemoryLoggerProvider()).SetMinimumLevel(LogLevel.Debug));

        services.AddRabbitFlow(settings =>
        {
            settings.ConfigureHost(h =>
            {
                h.Host = Host;
                h.Port = Port;
                h.Username = Username;
                h.Password = Password;
            });

            configure(settings);
        });

        services.UseRabbitFlowConsumers();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Forces the broker to drop every open connection (consumers, publisher, verification),
    /// simulating an abrupt network/broker outage so recovery paths can be exercised.
    /// Returns the <c>rabbitmqctl</c> exit code (0 on success).
    /// </summary>
    public async Task<long> CloseAllConnectionsAsync(string reason = "regression-test")
    {
        var result = await _container.ExecAsync(new[] { "rabbitmqctl", "close_all_connections", reason });
        return result.ExitCode ?? -1;
    }

    /// <summary>
    /// Creates a direct RabbitMQ connection for test verification.
    /// </summary>
    public async Task<IConnection> CreateDirectConnectionAsync(CancellationToken ct = default)
    {
        var factory = new ConnectionFactory
        {
            HostName = Host,
            Port = Port,
            UserName = Username,
            Password = Password
        };
        return await factory.CreateConnectionAsync("test-verification", ct);
    }
}

[CollectionDefinition("RabbitMq")]
public class RabbitMqCollection : ICollectionFixture<RabbitMqFixture>
{
}
