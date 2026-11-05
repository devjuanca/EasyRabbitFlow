using EasyRabbitFlow;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
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
    private readonly RabbitMqContainer _container = BuildContainer();

    private static string ResolveImage()
    {
        var image = Environment.GetEnvironmentVariable("RABBITMQ_IMAGE");
        return string.IsNullOrWhiteSpace(image) ? "rabbitmq:4.3-management" : image;
    }

    /// <summary>
    /// True when the broker under test declares every queue without an explicit <c>x-queue-type</c> as a
    /// <b>quorum</b> queue (RABBITMQ_DEFAULT_QUEUE_TYPE=quorum). Mirrors a cluster or vhost configured with
    /// <c>default_queue_type = quorum</c>, where the library's auto-generated queues silently become quorum
    /// queues: no exclusive/non-durable queues, consumer timeouts enforced by the queue (4.3+), delivery limit.
    /// </summary>
    public static bool QuorumByDefault =>
        string.Equals(Environment.GetEnvironmentVariable("RABBITMQ_DEFAULT_QUEUE_TYPE"), "quorum", StringComparison.OrdinalIgnoreCase);

    private static RabbitMqContainer BuildContainer()
    {
        var builder = new RabbitMqBuilder(ResolveImage())
            .WithUsername("guest")
            .WithPassword("guest");

        if (QuorumByDefault)
        {
            // The official image loads every /etc/rabbitmq/conf.d/*.conf; 10-defaults.conf ships with the image.
            builder = builder.WithResourceMapping(
                System.Text.Encoding.UTF8.GetBytes("default_queue_type = quorum\n"),
                "/etc/rabbitmq/conf.d/20-default-queue-type.conf");
        }

        return builder.Build();
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
    /// <summary>
    /// Builds a service provider with consumer hosted service registered. <paramref name="configureHost"/> tweaks
    /// the host settings (e.g. <c>NetworkRecoveryInterval</c>) on top of the container's connection details: a second
    /// <c>ConfigureHost</c> call from <paramref name="configure"/> would replace them with the defaults (localhost).
    /// </summary>
    public IServiceProvider BuildServiceProviderWithConsumers(Action<RabbitFlowConfigurator> configure, Action<HostSettings>? configureHost = null)
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

                configureHost?.Invoke(h);
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
    /// Returns the broker's view of a queue's type ("classic", "quorum", "stream") via
    /// <c>rabbitmqctl list_queues</c>, or <c>null</c> when the queue does not exist.
    /// </summary>
    public async Task<string?> GetQueueTypeAsync(string queueName)
    {
        var result = await _container.ExecAsync(new[] { "rabbitmqctl", "--quiet", "list_queues", "name", "type" });

        foreach (var line in result.Stdout.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\t');

            if (parts.Length >= 2 && parts[0] == queueName)
            {
                return parts[1].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Raw <c>rabbitmqctl list_queues</c> table (name, type, messages, unacked, consumers) for failure diagnostics.
    /// </summary>
    public async Task<string> DumpQueuesAsync(string? nameFilter = null)
    {
        var result = await _container.ExecAsync(new[] { "rabbitmqctl", "--quiet", "list_queues", "name", "type", "messages", "messages_unacknowledged", "consumers" });

        var lines = result.Stdout.Split('\n').Where(l => nameFilter == null || l.Contains(nameFilter));

        return string.Join("\n", lines);
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
