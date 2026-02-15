using EasyRabbitFlow;
using EasyRabbitFlow.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace EasyRabbitFlow.Tests.Fixtures;

public class RabbitMqFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:3.13-management")
        .WithUsername("guest")
        .WithPassword("guest")
        .Build();

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

            configure(settings);
        });

        services.UseRabbitFlowConsumers();

        return services.BuildServiceProvider();
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
