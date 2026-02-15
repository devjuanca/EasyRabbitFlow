using System.Text;
using System.Text.Json;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Tests.Fixtures;
using EasyRabbitFlow.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;

namespace EasyRabbitFlow.Tests;

[Collection("RabbitMq")]
public class ConsumerTests
{
    private readonly RabbitMqFixture _fixture;

    public ConsumerTests(RabbitMqFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Consumer_ReceivesPublishedMessage()
    {
        // Arrange
        TestConsumer.Reset();

        var queueName = $"test-consumer-{Guid.NewGuid():N}";

        var sp = _fixture.BuildServiceProviderWithConsumers(settings =>
        {
            settings.AddConsumer<TestConsumer>(queueName, cfg =>
            {
                cfg.AutoGenerate = true;
                cfg.PrefetchCount = 1;
                cfg.Timeout = TimeSpan.FromSeconds(10);
                cfg.ConfigureAutoGenerate(ag =>
                {
                    ag.GenerateExchange = false;
                    ag.GenerateDeadletterQueue = false;
                    ag.DurableQueue = false;
                    ag.AutoDeleteQueue = true;
                });
            });
        });

        // Start hosted service
        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);

        // Give consumer time to start
        await Task.Delay(500);

        // Act - publish a message directly to the queue
        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();

        var evt = new TestEvent { Id = "consumer-test-1", Message = "hello-consumer" };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(evt, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        await ch.BasicPublishAsync("", queueName, body);

        // Wait for message to be consumed
        var timeout = TimeSpan.FromSeconds(10);
        var deadline = DateTime.UtcNow + timeout;
        while (TestConsumer.ReceivedMessages.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        // Assert
        Assert.Single(TestConsumer.ReceivedMessages);
        Assert.Equal("consumer-test-1", TestConsumer.ReceivedMessages[0].Id);
        Assert.Equal("hello-consumer", TestConsumer.ReceivedMessages[0].Message);

        // Cleanup
        await hostedService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Consumer_WithAutoGenerate_CreatesQueueAndExchange()
    {
        // Arrange
        var queueName = $"test-autogen-{Guid.NewGuid():N}";

        var sp = _fixture.BuildServiceProviderWithConsumers(settings =>
        {
            settings.AddConsumer<TestConsumer>(queueName, cfg =>
            {
                cfg.AutoGenerate = true;
                cfg.PrefetchCount = 1;
                cfg.Timeout = TimeSpan.FromSeconds(10);
                cfg.ConfigureAutoGenerate(ag =>
                {
                    ag.GenerateExchange = true;
                    ag.ExchangeType = EasyRabbitFlow.Settings.ExchangeType.Direct;
                    ag.GenerateDeadletterQueue = true;
                    ag.DurableQueue = false;
                    ag.DurableExchange = false;
                    ag.AutoDeleteQueue = true;
                });
            });
        });

        // Act - start hosted service (which creates topology)
        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        // Assert - verify queue was created by checking we can access its info
        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();

        // If queue exists, this will succeed. If not, it throws.
        var queueOk = await ch.QueueDeclarePassiveAsync(queueName);
        Assert.Equal(queueName, queueOk.QueueName);

        // Also verify the dead-letter queue was created
        var dlqName = $"{queueName}-deadletter";
        var dlqOk = await ch.QueueDeclarePassiveAsync(dlqName);
        Assert.Equal(dlqName, dlqOk.QueueName);

        // Cleanup
        await hostedService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Consumer_WithRetryPolicy_RetriesOnTransientException()
    {
        // Arrange
        TransientFailConsumer.Reset();

        var queueName = $"test-retry-{Guid.NewGuid():N}";

        var sp = _fixture.BuildServiceProviderWithConsumers(settings =>
        {
            settings.AddConsumer<TransientFailConsumer>(queueName, cfg =>
            {
                cfg.AutoGenerate = true;
                cfg.PrefetchCount = 1;
                cfg.Timeout = TimeSpan.FromSeconds(10);
                cfg.ConfigureRetryPolicy(r =>
                {
                    r.MaxRetryCount = 3;
                    r.RetryInterval = 100;
                    r.ExponentialBackoff = false;
                });
                cfg.ConfigureAutoGenerate(ag =>
                {
                    ag.GenerateExchange = false;
                    ag.GenerateDeadletterQueue = false;
                    ag.DurableQueue = false;
                    ag.AutoDeleteQueue = true;
                });
            });
        });

        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        // Act
        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();

        var evt = new TestEvent { Id = "retry-test-1", Message = "retry-me" };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(evt, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        await ch.BasicPublishAsync("", queueName, body);

        // Wait for retries to complete
        var timeout = TimeSpan.FromSeconds(15);
        var deadline = DateTime.UtcNow + timeout;
        while (TransientFailConsumer.ReceivedMessages.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        // Assert - The message should eventually succeed after retry
        Assert.Single(TransientFailConsumer.ReceivedMessages);
        Assert.Equal("retry-test-1", TransientFailConsumer.ReceivedMessages[0].Id);

        // Cleanup
        await hostedService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Consumer_PublishAndConsume_EndToEnd()
    {
        // Arrange
        TestConsumer.Reset();

        var queueName = $"test-e2e-{Guid.NewGuid():N}";

        var sp = _fixture.BuildServiceProviderWithConsumers(settings =>
        {
            settings.AddConsumer<TestConsumer>(queueName, cfg =>
            {
                cfg.AutoGenerate = true;
                cfg.PrefetchCount = 5;
                cfg.Timeout = TimeSpan.FromSeconds(10);
                cfg.ConfigureAutoGenerate(ag =>
                {
                    ag.GenerateExchange = false;
                    ag.GenerateDeadletterQueue = false;
                    ag.DurableQueue = false;
                    ag.AutoDeleteQueue = true;
                });
            });
        });

        // Start consumers
        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        // Act - use the library's own publisher to publish multiple messages
        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();

        for (int i = 0; i < 10; i++)
        {
            await publisher.PublishAsync(
                new TestEvent { Id = i.ToString(), Message = $"e2e-{i}" },
                queueName);
        }

        // Wait for all messages to be consumed
        var timeout = TimeSpan.FromSeconds(15);
        var deadline = DateTime.UtcNow + timeout;
        while (TestConsumer.ReceivedMessages.Count < 10 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        // Assert
        Assert.Equal(10, TestConsumer.ReceivedMessages.Count);

        // Cleanup
        await hostedService.StopAsync(CancellationToken.None);
    }
}
