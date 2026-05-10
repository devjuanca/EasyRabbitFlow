using System.Text;
using System.Text.Json;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
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

    [Fact]
    public async Task Consumer_WrappedDeadLetterEnvelope_DefaultSetting_DoesNotUnwrap()
    {
        // Arrange - same scenario as the unwrap test, but the opt-in flag is left at its default (false).
        // Expectation: the envelope is NOT detected; the body deserializes as a default TestEvent
        // (envelope keys don't map to TestEvent), and the consumer receives it with null Id/Message.
        TestConsumer.Reset();

        var queueName = $"test-no-unwrap-default-{Guid.NewGuid():N}";

        var sp = _fixture.BuildServiceProviderWithConsumers(settings =>
        {
            settings.AddConsumer<TestConsumer>(queueName, cfg =>
            {
                cfg.AutoGenerate = true;
                cfg.PrefetchCount = 1;
                cfg.Timeout = TimeSpan.FromSeconds(10);
                // UnwrapDeadLetterEnvelopes NOT set - default is false.
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

        var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        var inner = new TestEvent { Id = "would-be-unwrapped", Message = "would-be-payload" };
        var innerJson = JsonSerializer.Serialize(inner, jsonOpts);

        var envelope = new DeadLetterEnvelope
        {
            DateUtc = DateTime.UtcNow,
            MessageType = nameof(TestEvent),
            MessageData = JsonSerializer.Deserialize<JsonElement>(innerJson),
            ExceptionType = "InvalidOperationException",
            ErrorMessage = "previous failure",
            ReprocessAttempts = 0
        };

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, jsonOpts));

        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();
        await ch.BasicPublishAsync("", queueName, body);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (TestConsumer.ReceivedMessages.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        // With the flag off, no unwrap fires. The envelope's top-level keys don't map to TestEvent's
        // properties, so deserialization yields a TestEvent with its property defaults intact -
        // explicitly NOT the inner payload's values.
        Assert.Single(TestConsumer.ReceivedMessages);
        Assert.NotEqual("would-be-unwrapped", TestConsumer.ReceivedMessages[0].Id);
        Assert.NotEqual("would-be-payload", TestConsumer.ReceivedMessages[0].Message);

        await hostedService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Consumer_WrappedDeadLetterEnvelope_IsUnwrappedAndProcessed()
    {
        // Arrange - simulates a client manually replaying a DeadLetterEnvelope from the DLQ to the main queue.
        TestConsumer.Reset();

        var queueName = $"test-unwrap-{Guid.NewGuid():N}";

        var sp = _fixture.BuildServiceProviderWithConsumers(settings =>
        {
            settings.AddConsumer<TestConsumer>(queueName, cfg =>
            {
                cfg.AutoGenerate = true;
                cfg.PrefetchCount = 1;
                cfg.Timeout = TimeSpan.FromSeconds(10);
                cfg.UnwrapDeadLetterEnvelopes = true;
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

        var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        var inner = new TestEvent { Id = "unwrap-1", Message = "wrapped-payload" };
        var innerJson = JsonSerializer.Serialize(inner, jsonOpts);

        var envelope = new DeadLetterEnvelope
        {
            DateUtc = DateTime.UtcNow,
            MessageType = nameof(TestEvent),
            MessageId = "msg-from-envelope",
            CorrelationId = "corr-from-envelope",
            MessageData = JsonSerializer.Deserialize<JsonElement>(innerJson),
            ExceptionType = "InvalidOperationException",
            ErrorMessage = "previous failure",
            ReprocessAttempts = 0
        };

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, jsonOpts));

        // Act - publish the wrapped envelope directly to the main queue.
        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();
        await ch.BasicPublishAsync("", queueName, body);

        // Assert - the consumer extracts the inner payload and processes it.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (TestConsumer.ReceivedMessages.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.Single(TestConsumer.ReceivedMessages);
        Assert.Equal("unwrap-1", TestConsumer.ReceivedMessages[0].Id);
        Assert.Equal("wrapped-payload", TestConsumer.ReceivedMessages[0].Message);

        await hostedService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Consumer_WrappedEnvelope_HandlerFails_DeadLetterDoesNotDoubleWrap()
    {
        // Arrange - a wrapped envelope is replayed to the main queue, but the consumer always fails.
        // The new DLQ entry must wrap the ORIGINAL payload (depth 1), not the inbound envelope (depth 2).
        AlwaysFailConsumer.Reset();

        var queueName = $"test-no-double-{Guid.NewGuid():N}";
        var dlqName = $"{queueName}-deadletter";

        var sp = _fixture.BuildServiceProviderWithConsumers(settings =>
        {
            settings.AddConsumer<AlwaysFailConsumer>(queueName, cfg =>
            {
                cfg.AutoGenerate = true;
                cfg.PrefetchCount = 1;
                cfg.Timeout = TimeSpan.FromSeconds(5);
                cfg.ExtendDeadletterMessage = true;
                cfg.UnwrapDeadLetterEnvelopes = true;
                cfg.ConfigureAutoGenerate(ag =>
                {
                    ag.GenerateExchange = false;
                    ag.GenerateDeadletterQueue = true;
                    ag.DurableQueue = false;
                    ag.DurableExchange = false;
                    ag.AutoDeleteQueue = true;
                });
            });
        });

        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        var inner = new TestEvent { Id = "no-double-1", Message = "should-be-unwrapped" };
        var innerJson = JsonSerializer.Serialize(inner, jsonOpts);

        var envelope = new DeadLetterEnvelope
        {
            DateUtc = DateTime.UtcNow,
            MessageType = nameof(TestEvent),
            MessageData = JsonSerializer.Deserialize<JsonElement>(innerJson),
            ExceptionType = "InvalidOperationException",
            ErrorMessage = "old-error",
            ReprocessAttempts = 0
        };

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, jsonOpts));

        // Act
        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();
        await ch.BasicPublishAsync("", queueName, body);

        // Wait until the message lands in the DLQ.
        BasicGetResult? dlqResult = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            dlqResult = await ch.BasicGetAsync(dlqName, autoAck: true);
            if (dlqResult != null) break;
            await Task.Delay(200);
        }

        // Assert - the consumer received the unwrapped TestEvent (proof unwrap fired).
        Assert.Single(AlwaysFailConsumer.ReceivedMessages);
        Assert.Equal("no-double-1", AlwaysFailConsumer.ReceivedMessages[0].Id);

        // The DLQ entry exists.
        Assert.NotNull(dlqResult);

        var dlqEnvelope = JsonSerializer.Deserialize<DeadLetterEnvelope>(dlqResult!.Body.Span, jsonOpts);
        Assert.NotNull(dlqEnvelope);
        Assert.NotNull(dlqEnvelope!.MessageData);

        // Critical: the DLQ envelope's MessageData is the original TestEvent JSON, not a nested envelope.
        var msgData = dlqEnvelope.MessageData!.Value;
        Assert.Equal(JsonValueKind.Object, msgData.ValueKind);

        Assert.True(msgData.TryGetProperty("id", out var idProp), "DLQ envelope should wrap the original TestEvent (with 'id'), not a nested envelope.");
        Assert.Equal("no-double-1", idProp.GetString());

        Assert.True(msgData.TryGetProperty("message", out var msgProp));
        Assert.Equal("should-be-unwrapped", msgProp.GetString());

        // And it must NOT carry envelope-level fields (which would indicate double-wrapping).
        Assert.False(msgData.TryGetProperty("messageData", out _));
        Assert.False(msgData.TryGetProperty("exceptionType", out _));

        // Sanity: the new envelope should reflect the FRESH failure, not the inbound one.
        Assert.NotEqual("old-error", dlqEnvelope.ErrorMessage);

        await hostedService.StopAsync(CancellationToken.None);
    }
}
