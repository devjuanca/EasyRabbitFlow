using EasyRabbitFlow.Services;
using EasyRabbitFlow.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EasyRabbitFlow.Tests;

[Collection("RabbitMq")]
public class HealthCheckTests
{
    private readonly RabbitMqFixture _fixture;

    public HealthCheckTests(RabbitMqFixture fixture)
    {
        _fixture = fixture;
    }

    private static HealthCheckContext CreateContext(IHealthCheck check) => new()
    {
        Registration = new HealthCheckRegistration("rabbitflow", check, failureStatus: null, tags: null)
    };

    private async Task<HealthCheckResult> RunCheckAsync(Action<RabbitFlowHealthCheckOptions>? configure = null)
    {
        var sp = _fixture.BuildServiceProvider();
        var state = sp.GetRequiredService<IRabbitFlowState>();

        var options = new RabbitFlowHealthCheckOptions();
        configure?.Invoke(options);

        var check = new RabbitFlowHealthCheck(state, options);

        return await check.CheckHealthAsync(CreateContext(check));
    }

    [Fact]
    public async Task NoQueuesConfigured_BrokerReachable_ReportsHealthy()
    {
        var result = await RunCheckAsync();

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task ExistingEmptyQueue_ReportsHealthy_WithData()
    {
        var queueName = $"test-hc-{Guid.NewGuid():N}";

        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();
        await ch.QueueDeclareAsync(queueName, durable: false, exclusive: false, autoDelete: true);

        var result = await RunCheckAsync(o => o.Queues.Add(queueName));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(true, result.Data[$"{queueName}.exists"]);
        Assert.Equal(0u, result.Data[$"{queueName}.messages"]);
    }

    [Fact]
    public async Task MissingQueue_ReportsUnhealthy()
    {
        var result = await RunCheckAsync(o => o.Queues.Add($"missing-{Guid.NewGuid():N}"));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("does not exist", result.Description);
    }

    [Fact]
    public async Task RequireConsumers_QueueWithoutConsumers_ReportsDegraded()
    {
        var queueName = $"test-hc-nocons-{Guid.NewGuid():N}";

        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();
        await ch.QueueDeclareAsync(queueName, durable: false, exclusive: false, autoDelete: true);

        var result = await RunCheckAsync(o =>
        {
            o.Queues.Add(queueName);
            o.RequireConsumers = true;
        });

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("has no consumers", result.Description);
    }

    [Fact]
    public async Task AddRabbitFlow_RegistersCheck_InHealthChecksPipeline()
    {
        // Full pipeline: AddRabbitFlow + AddHealthChecks().AddRabbitFlow() resolved through DI
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRabbitFlow(settings => settings.ConfigureHost(h =>
        {
            h.Host = _fixture.Host;
            h.Port = _fixture.Port;
            h.Username = _fixture.Username;
            h.Password = _fixture.Password;
        }));

        services.AddHealthChecks().AddRabbitFlow();

        var sp = services.BuildServiceProvider();
        var healthService = sp.GetRequiredService<HealthCheckService>();

        var report = await healthService.CheckHealthAsync();

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Contains("rabbitflow", report.Entries.Keys);
    }
}
