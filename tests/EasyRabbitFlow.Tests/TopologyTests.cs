using EasyRabbitFlow.Exceptions;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Tests.Fixtures;
using EasyRabbitFlow.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;

namespace EasyRabbitFlow.Tests;

[Collection("RabbitMq")]
public class TopologyTests
{
    private readonly RabbitMqFixture _fixture;

    public TopologyTests(RabbitMqFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DeclareExchange_DeclaresExchanges_WithoutConsumers()
    {
        // Arrange: publisher-only provider — no AddConsumer, no UseRabbitFlowConsumers.
        var directExchange = $"topology-direct-{Guid.NewGuid():N}";
        var topicExchange = $"topology-topic-{Guid.NewGuid():N}";

        var sp = _fixture.BuildServiceProvider(settings =>
        {
            settings.DeclareExchange(directExchange);
            settings.DeclareExchange(topicExchange, Settings.ExchangeType.Topic, opts =>
            {
                opts.Durable = false;
                opts.AutoDelete = true;
            });
        });

        var hostedService = sp.GetServices<IHostedService>().OfType<TopologyHostedService>().Single();

        // Act
        await hostedService.StartAsync(CancellationToken.None);

        // Assert: both exchanges exist (passive declare throws 404 if missing).
        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();
        await ch.ExchangeDeclarePassiveAsync(directExchange);
        await ch.ExchangeDeclarePassiveAsync(topicExchange);

        // And an externally-bound queue receives what the publisher sends — the actual use case.
        var queueName = $"external-client-{Guid.NewGuid():N}";
        await ch.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: true);
        await ch.QueueBindAsync(queueName, topicExchange, "orders.*.created");

        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();
        var result = await publisher.PublishAsync(new TestEvent { Id = "1", Message = "topology" }, topicExchange, routingKey: "orders.eu.created");

        Assert.True(result.Success);

        await Task.Delay(200);
        var messageCount = await ch.MessageCountAsync(queueName);
        Assert.True(messageCount >= 1, $"Expected at least 1 routed message, found {messageCount}");
    }

    [Fact]
    public async Task DeclareExchange_ExistingExchangeWithDifferentType_AdoptsAndContinues()
    {
        // Arrange: the exchange already exists as fanout; the app declares it as direct (mismatch → 406).
        var mismatchedExchange = $"topology-mismatch-{Guid.NewGuid():N}";
        var healthyExchange = $"topology-healthy-{Guid.NewGuid():N}";

        using (var setupConn = await _fixture.CreateDirectConnectionAsync())
        using (var setupCh = await setupConn.CreateChannelAsync())
        {
            await setupCh.ExchangeDeclareAsync(mismatchedExchange, "fanout", durable: true, autoDelete: false);
        }

        var sp = _fixture.BuildServiceProvider(settings =>
        {
            settings.DeclareExchange(mismatchedExchange, Settings.ExchangeType.Direct);
            settings.DeclareExchange(healthyExchange);
        });

        var hostedService = sp.GetServices<IHostedService>().OfType<TopologyHostedService>().Single();

        // Act: must not throw despite the mismatch.
        await hostedService.StartAsync(CancellationToken.None);

        // Assert: the existing exchange was adopted (still fanout, still there) and the mismatch did not
        // prevent the remaining exchange from being declared.
        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();
        await ch.ExchangeDeclarePassiveAsync(mismatchedExchange);
        await ch.ExchangeDeclarePassiveAsync(healthyExchange);
    }

    [Fact]
    public void DeclareExchange_ManyCalls_RegistersHostedServiceOnce()
    {
        var sp = _fixture.BuildServiceProvider(settings =>
        {
            settings.DeclareExchange($"topology-multi-a-{Guid.NewGuid():N}");
            settings.DeclareExchange($"topology-multi-b-{Guid.NewGuid():N}", Settings.ExchangeType.Fanout);
        });

        Assert.Single(sp.GetServices<IHostedService>().OfType<TopologyHostedService>());
    }

    [Fact]
    public void DeclareExchange_EmptyName_Throws()
    {
        var configurator = new RabbitFlowConfigurator(new ServiceCollection());

        Assert.Throws<RabbitFlowException>(() => configurator.DeclareExchange(""));
        Assert.Throws<RabbitFlowException>(() => configurator.DeclareExchange("   "));
    }

    [Fact]
    public void DeclareExchange_ReservedDeadletterSubstring_Throws()
    {
        var configurator = new RabbitFlowConfigurator(new ServiceCollection());

        Assert.Throws<RabbitFlowException>(() => configurator.DeclareExchange("orders-deadletter-exchange"));
        Assert.Throws<RabbitFlowException>(() => configurator.DeclareExchange("MyDeadLetterHub"));
    }

    [Fact]
    public void DeclareExchange_DuplicateName_Throws()
    {
        var configurator = new RabbitFlowConfigurator(new ServiceCollection());

        configurator.DeclareExchange("orders-events");

        Assert.Throws<RabbitFlowException>(() => configurator.DeclareExchange("orders-events", Settings.ExchangeType.Topic));
    }
}
