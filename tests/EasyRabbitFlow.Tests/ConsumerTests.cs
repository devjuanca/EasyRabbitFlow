using System.Text;
using System.Text.Json;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using EasyRabbitFlow.Tests.Fixtures;
using EasyRabbitFlow.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Xunit.Abstractions;

namespace EasyRabbitFlow.Tests;

[Collection("RabbitMq")]
public class ConsumerTests
{
    private readonly RabbitMqFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ConsumerTests(RabbitMqFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
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
                    ag.DurableQueue = true;
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
                    ag.DurableQueue = true;
                    ag.DurableExchange = true;
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
#pragma warning disable CS0618 // in-process retries are deprecated but still supported until the next major
                cfg.ConfigureRetryPolicy(r =>
                {
                    r.MaxRetryCount = 3;
                    r.RetryInterval = 100;
                });
#pragma warning restore CS0618
                cfg.ConfigureAutoGenerate(ag =>
                {
                    ag.GenerateExchange = false;
                    ag.GenerateDeadletterQueue = false;
                    ag.DurableQueue = true;
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
                    ag.DurableQueue = true;
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
                    ag.DurableQueue = true;
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
                    ag.DurableQueue = true;
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
                    ag.DurableQueue = true;
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

    [Fact]
    public async Task Consumer_StopAsync_IsIdempotent_SequentialCalls()
    {
        // Regression: WebApplicationFactory teardown paths can invoke Host.StopAsync more than once.
        // The second call used to throw ObjectDisposedException because the internal semaphores
        // were disposed by the first call.
        var queueName = $"test-stop-idem-seq-{Guid.NewGuid():N}";

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
                    ag.DurableQueue = true;
                    ag.AutoDeleteQueue = true;
                });
            });
        });

        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        await hostedService.StopAsync(CancellationToken.None);
        await hostedService.StopAsync(CancellationToken.None);
        await hostedService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Consumer_StopAsync_IsIdempotent_ConcurrentCalls()
    {
        // Regression: two teardown paths racing on the same hosted service must not corrupt state.
        var queueName = $"test-stop-idem-conc-{Guid.NewGuid():N}";

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
                    ag.DurableQueue = true;
                    ag.AutoDeleteQueue = true;
                });
            });
        });

        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        var stopA = hostedService.StopAsync(CancellationToken.None);
        var stopB = hostedService.StopAsync(CancellationToken.None);

        await Task.WhenAll(stopA, stopB);
    }

    [Fact]
    public async Task Consumer_RecoversAfterConnectionDrop_AndKeepsConsuming()
    {
        // Regression (v8 / 8.0.0-rc.2): a consumer must recover after its connection is dropped and keep
        // consuming, WITHOUT a topology error.
        //
        // In 8.0.0-rc.1 the main queue was declared on a throwaway channel and the RabbitMQ client's built-in
        // topology recovery was left enabled. On reconnect the client re-bound/re-consumed a recorded topology
        // that did NOT include the main queue (it was never recorded, the throwaway declare adopted a 406), so
        // recovery failed with "404 NOT_FOUND - no queue '...'" on queue.bind and the consumer stayed dead.
        // EasyRabbitFlow now owns recovery (client recovery is off) and re-declares the FULL topology on every
        // recovery, so the consumer comes back. This test exercises the exact path that broke: AutoGenerate with
        // an exchange + binding + dead-letter queue.
        TestConsumer.Reset();
        EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Reset();

        var queueName = $"test-recovery-{Guid.NewGuid():N}";

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
                    ag.DurableQueue = true;
                    ag.DurableExchange = false;
                    // Must survive the connection drop so recovery re-binds to the SAME existing queue
                    // (the scenario that produced the 404). An auto-delete queue would be removed when the
                    // consumer disconnects, changing the test into a create-from-scratch path.
                    ag.AutoDeleteQueue = false;
                });
            });
        });

        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();

        // Sanity: a message flows before the drop.
        await publisher.PublishAsync(new TestEvent { Id = "before", Message = "before-drop" }, queueName);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (TestConsumer.ReceivedMessages.Count < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert.Single(TestConsumer.ReceivedMessages);

        // Act - force the broker to drop every connection, including the consumer's.
        var exitCode = await _fixture.CloseAllConnectionsAsync();
        Assert.Equal(0, exitCode);

        // Give the library's recovery loop time to re-establish the connection, channel and topology.
        await Task.Delay(3000);

        // Assert - a message published AFTER the drop is consumed, proving the consumer recovered and
        // re-bound/re-consumed without a 404. (PublishAsync also exercises the publisher's connection
        // re-creation, since its long-lived connection was dropped too.)
        var afterResult = await publisher.PublishAsync(new TestEvent { Id = "after", Message = "after-drop" }, queueName);

        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (TestConsumer.ReceivedMessages.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        uint depth; uint consumers;
        using (var probeConn = await _fixture.CreateDirectConnectionAsync())
        using (var probeCh = await probeConn.CreateChannelAsync())
        {
            var ok = await probeCh.QueueDeclarePassiveAsync(queueName);
            depth = ok.MessageCount;
            consumers = ok.ConsumerCount;
        }

        Assert.True(
            TestConsumer.ReceivedMessages.Count == 2 && TestConsumer.ReceivedMessages.Any(m => m.Id == "after"),
            $"Consumer did not recover. Received={TestConsumer.ReceivedMessages.Count}, afterPublishSuccess={afterResult.Success}, queueDepth={depth}, consumers={consumers}.\nLOGS:\n{EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Dump()}");

        // Cleanup
        await hostedService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Consumer_AdoptsPreexistingQueueWithConflictingArgs_WithoutCrashing()
    {
        // Regression (v8): when the auto-generated queue already exists with arguments that differ from the
        // current configuration (e.g. a previous run created it transient + auto-delete + without a dead-letter
        // exchange), the consumer must adopt the existing queue and keep running instead of letting the declare
        // mismatch (PRECONDITION_FAILED / 406) propagate and crash host startup. This is the visible half of the
        // QA failure: the "[RABBIT-FLOW]: Queue '...' already exists with arguments that differ" warning followed
        // by a hard 404 that took the host down.
        TestConsumer.Reset();

        var queueName = $"test-adopt-{Guid.NewGuid():N}";

        // Seed the queue with arguments that differ from what the consumer requests below (consumer wants a
        // durable queue with a dead-letter exchange; this one is transient, auto-delete and has no DLX), so the
        // consumer's declare hits a 406 and must take the adopt path.
        using (var seedConn = await _fixture.CreateDirectConnectionAsync())
        using (var seedCh = await seedConn.CreateChannelAsync())
        {
            await seedCh.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: true, arguments: null);
        }

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
                    ag.GenerateDeadletterQueue = true;   // adds x-dead-letter-* args -> mismatch vs the seeded queue
                    ag.DurableQueue = true;              // mismatch: seeded queue is transient
                    ag.DurableExchange = false;
                    ag.AutoDeleteQueue = false;          // mismatch: seeded queue is auto-delete
                });
            });
        });

        var hostedService = sp.GetServices<IHostedService>().First();

        // Must NOT throw: the 406 is adopted, not propagated to the host.
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        // The adopted queue is usable end-to-end.
        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();
        await publisher.PublishAsync(new TestEvent { Id = "adopt-1", Message = "adopted" }, queueName);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (TestConsumer.ReceivedMessages.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.Single(TestConsumer.ReceivedMessages);
        Assert.Equal("adopt-1", TestConsumer.ReceivedMessages[0].Id);

        await hostedService.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(typeof(AlwaysDerivedTransientFailConsumer), true)]   // subclass of RabbitFlowTransientException
    [InlineData(typeof(AlwaysFailConsumer), false)]                  // InvalidOperationException
    public async Task DeadLetterEnvelope_Carries_IsTransient_Flag(Type consumerType, bool expectedTransient)
    {
        // Arrange
        AlwaysFailConsumer.Reset();

        var queueName = $"test-istransient-{Guid.NewGuid():N}";
        var dlqName = $"{queueName}-deadletter";

        void Configure(RabbitFlowConfigurator settings)
        {
            void Common<TConsumer>() where TConsumer : class, IRabbitFlowConsumer<TestEvent> =>
                settings.AddConsumer<TConsumer>(queueName, cfg =>
                {
                    cfg.AutoGenerate = true;
                    cfg.PrefetchCount = 1;
                    cfg.Timeout = TimeSpan.FromSeconds(5);
                    cfg.ExtendDeadletterMessage = true;
                    cfg.ConfigureAutoGenerate(ag =>
                    {
                        ag.GenerateExchange = false;
                        ag.GenerateDeadletterQueue = true;
                        ag.DurableQueue = true;
                        ag.DurableExchange = false;
                        ag.AutoDeleteQueue = true;
                    });
                });

            if (consumerType == typeof(AlwaysDerivedTransientFailConsumer))
            {
                Common<AlwaysDerivedTransientFailConsumer>();
            }
            else
            {
                Common<AlwaysFailConsumer>();
            }
        }

        var sp = _fixture.BuildServiceProviderWithConsumers(Configure);

        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        // Act
        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();

        var evt = new TestEvent { Id = "transient-flag-1", Message = "fails" };
        await ch.BasicPublishAsync("", queueName, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(evt, jsonOpts)));

        BasicGetResult? dlqResult = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            dlqResult = await ch.BasicGetAsync(dlqName, autoAck: true);
            if (dlqResult != null) break;
            await Task.Delay(200);
        }

        // Assert
        Assert.NotNull(dlqResult);

        // Envelope is published persistent so it survives a broker restart while it waits in the durable
        // DLQ for the next reprocessor cycle.
        Assert.Equal(DeliveryModes.Persistent, dlqResult!.BasicProperties.DeliveryMode);

        var envelope = JsonSerializer.Deserialize<DeadLetterEnvelope>(dlqResult!.Body.Span, jsonOpts);
        Assert.NotNull(envelope);
        Assert.Equal(expectedTransient, envelope!.IsTransient);

        if (expectedTransient)
        {
            // Inheritance-aware: the runtime type name is the derived one, which the legacy
            // name-matching would have misclassified as permanent.
            Assert.Equal(nameof(DerivedTransientException), envelope.ExceptionType);
        }

        await hostedService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Consumer_ResubscribesAfterBrokerCancelsConsumer()
    {
        // The broker cancels a consumer (basic.cancel) without closing its channel when the queue is deleted,
        // when the node hosting the queue goes away, or — on RabbitMQ 4.3+ quorum queues — when a delivery
        // exceeds the consumer acknowledgement timeout. The channel and connection stay open, so neither
        // ChannelShutdownAsync nor ConnectionShutdownAsync fires: a consumer that only listens to those is left
        // attached to a live channel that delivers nothing, until the process restarts. Deleting the queue is the
        // deterministic way to trigger that notification on every broker version.
        TestConsumer.Reset();
        EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Reset();

        var queueName = $"test-cancel-{Guid.NewGuid():N}";

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
                    ag.DurableQueue = true;
                    ag.DurableExchange = false;
                    ag.AutoDeleteQueue = false;
                });
            });
        }, host => host.NetworkRecoveryInterval = TimeSpan.FromSeconds(1));

        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();

        // Sanity: a message flows before the cancel.
        await publisher.PublishAsync(new TestEvent { Id = "before", Message = "before-cancel" }, queueName);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (TestConsumer.ReceivedMessages.Count < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert.Single(TestConsumer.ReceivedMessages);

        // Act - delete the queue from another connection. The broker sends basic.cancel to our consumer; its
        // channel and connection remain open.
        using (var adminConn = await _fixture.CreateDirectConnectionAsync())
        using (var adminCh = await adminConn.CreateChannelAsync())
        {
            await adminCh.QueueDeleteAsync(queueName);
        }

        // Wait for the library to notice the cancel and re-declare + re-consume. Deleting a quorum queue under a
        // live consumer can also make the broker drop the connection (541 INTERNAL_ERROR on 4.3.x), which turns the
        // re-subscribe into a full recovery with backoff, so poll rather than sleep.
        uint consumers = 0;
        string probeOutcome = "not probed";
        var probeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (consumers != 1 && DateTime.UtcNow < probeDeadline)
        {
            await Task.Delay(250);

            try
            {
                using var probeConn = await _fixture.CreateDirectConnectionAsync();
                using var probeCh = await probeConn.CreateChannelAsync();
                var ok = await probeCh.QueueDeclarePassiveAsync(queueName);
                consumers = ok.ConsumerCount;
                probeOutcome = $"exists, consumers={consumers}";
            }
            catch (RabbitMQ.Client.Exceptions.OperationInterruptedException ex)
            {
                // Without cancel handling the queue is simply gone (404) for good.
                probeOutcome = $"passive declare failed: {ex.ShutdownReason?.ReplyCode} {ex.ShutdownReason?.ReplyText}";
            }
        }

        // Assert - the queue exists again with exactly one consumer and a message published after the cancel
        // is consumed.
        var afterResult = await publisher.PublishAsync(new TestEvent { Id = "after", Message = "after-cancel" }, queueName);

        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (TestConsumer.ReceivedMessages.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.True(
            consumers == 1 && TestConsumer.ReceivedMessages.Any(m => m.Id == "after"),
            $"Consumer did not re-subscribe after basic.cancel. Queue: {probeOutcome}. Received={TestConsumer.ReceivedMessages.Count}, afterPublishSuccess={afterResult.Success}.\nLOGS:\n{EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Dump()}");

        await hostedService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Consumer_DeadLetterQueueDeletedAtRuntime_RecreatesItAndParksTheEnvelope()
    {
        // The dead-letter envelope is published on a confirm channel, but a publisher confirm also acknowledges a
        // message the broker dropped for having no route. If the dead-letter queue (and with it the binding) is gone,
        // the consumer must not ack the original on the strength of that confirm: it re-declares the dead-letter
        // topology and publishes the envelope again, so the failed message ends up parked and the main queue empty.
        AlwaysFailConsumer.Reset();
        EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Reset();

        var queueName = $"test-dlq-gone-{Guid.NewGuid():N}";
        var deadLetterQueueName = EasyRabbitFlow.Settings.RabbitFlowTopologyNames.DeadLetterQueue(queueName);

        var sp = _fixture.BuildServiceProviderWithConsumers(settings =>
        {
            settings.AddConsumer<AlwaysFailConsumer>(queueName, cfg =>
            {
                cfg.AutoGenerate = true;
                cfg.PrefetchCount = 1;
                cfg.Timeout = TimeSpan.FromSeconds(10);
                cfg.ConfigureAutoGenerate(ag =>
                {
                    ag.GenerateExchange = false;
                    ag.GenerateDeadletterQueue = true;
                    ag.DurableQueue = true;
                    ag.AutoDeleteQueue = false;
                });
            });
        });

        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        // Act - remove the dead-letter queue from another connection; the broker drops its binding with it, so the
        // dead-letter exchange has nowhere to route the envelope.
        using (var adminConn = await _fixture.CreateDirectConnectionAsync())
        using (var adminCh = await adminConn.CreateChannelAsync())
        {
            await adminCh.QueueDeleteAsync(deadLetterQueueName);
        }

        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();
        var result = await publisher.PublishAsync(new TestEvent { Id = "boom", Message = "fails" }, queueName, messageId: "msg-dlq-gone");
        Assert.True(result.Success);

        // Assert - the envelope lands in a recreated dead-letter queue and the main queue is left empty.
        uint parked = 0;
        string probeOutcome = "not probed";
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (parked < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);

            try
            {
                using var probeConn = await _fixture.CreateDirectConnectionAsync();
                using var probeCh = await probeConn.CreateChannelAsync();
                var ok = await probeCh.QueueDeclarePassiveAsync(deadLetterQueueName);
                parked = ok.MessageCount;
                probeOutcome = $"exists, messages={parked}";
            }
            catch (RabbitMQ.Client.Exceptions.OperationInterruptedException ex)
            {
                probeOutcome = $"passive declare failed: {ex.ShutdownReason?.ReplyCode} {ex.ShutdownReason?.ReplyText}";
            }
        }

        Assert.True(parked == 1,
            $"The envelope did not reach a recreated dead-letter queue. DLQ: {probeOutcome}.\nLOGS:\n{EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Dump()}");

        Assert.Contains(EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Lines, l => l.Contains("was returned by the broker as unroutable"));

        using (var probeConn = await _fixture.CreateDirectConnectionAsync())
        using (var probeCh = await probeConn.CreateChannelAsync())
        {
            var main = await probeCh.QueueDeclarePassiveAsync(queueName);
            Assert.Equal(0u, main.MessageCount);
        }

        await hostedService.StopAsync(CancellationToken.None);
    }

    [SkippableFact]
    public async Task Consumer_WithAutoGenerate_OnQuorumByDefaultBroker_GetsQuorumQueues()
    {
        // Guards the quorum CI leg itself: when the broker's default_queue_type is quorum, every queue the library
        // declares without x-queue-type (main queue and dead-letter queue alike) is a quorum queue. If this fails
        // the leg is not exercising quorum at all.
        Skip.IfNot(RabbitMqFixture.QuorumByDefault, "Only meaningful when RABBITMQ_DEFAULT_QUEUE_TYPE=quorum.");

        var queueName = $"test-qq-default-{Guid.NewGuid():N}";

        var sp = _fixture.BuildServiceProviderWithConsumers(settings =>
        {
            settings.AddConsumer<TestConsumer>(queueName, cfg =>
            {
                cfg.AutoGenerate = true;
                cfg.ConfigureAutoGenerate(ag =>
                {
                    ag.GenerateExchange = false;
                    ag.GenerateDeadletterQueue = true;
                    ag.DurableQueue = true;
                    ag.AutoDeleteQueue = false;
                });
            });
        });

        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        Assert.Equal("quorum", await _fixture.GetQueueTypeAsync(queueName));
        Assert.Equal("quorum", await _fixture.GetQueueTypeAsync($"{queueName}-deadletter"));

        await hostedService.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(QueueType.Classic, null, "classic", "classic")]
    [InlineData(QueueType.Quorum, null, "quorum", "quorum")]
    [InlineData(QueueType.Classic, QueueType.Quorum, "classic", "quorum")]
    [InlineData(QueueType.Quorum, QueueType.Classic, "quorum", "classic")]
    public async Task Consumer_WithQueueType_DeclaresMainAndDeadLetterQueuesWithThatType(QueueType queueType, QueueType? deadLetterQueueType, string expectedMain, string expectedDeadLetter)
    {
        // The explicit type is what the broker records, whatever its default_queue_type is; the DLQ follows
        // QueueType unless DeadLetterQueueType overrides it. Dead-lettering must work across types.
        TestConsumer.Reset();
        AlwaysFailConsumer.Reset();

        var queueName = $"test-qtype-{Guid.NewGuid():N}";

        var sp = _fixture.BuildServiceProviderWithConsumers(settings =>
        {
            settings.AddConsumer<AlwaysFailConsumer>(queueName, cfg =>
            {
                cfg.AutoGenerate = true;
                cfg.ExtendDeadletterMessage = false;
                cfg.Timeout = TimeSpan.FromSeconds(10);
                cfg.ConfigureAutoGenerate(ag =>
                {
                    ag.GenerateExchange = false;
                    ag.GenerateDeadletterQueue = true;
                    ag.DurableQueue = true;
                    ag.AutoDeleteQueue = false;
                    ag.QueueType = queueType;
                    ag.DeadLetterQueueType = deadLetterQueueType;
                });
            });
        });

        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        Assert.Equal(expectedMain, await _fixture.GetQueueTypeAsync(queueName));
        Assert.Equal(expectedDeadLetter, await _fixture.GetQueueTypeAsync($"{queueName}-deadletter"));

        // A failing handler dead-letters the raw message (ExtendDeadletterMessage = false → broker nack) into a DLQ
        // of possibly another type.
        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();
        await publisher.PublishAsync(new TestEvent { Id = "dl", Message = "cross-type" }, queueName);

        using var probeConn = await _fixture.CreateDirectConnectionAsync();
        using var probeCh = await probeConn.CreateChannelAsync();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        uint dlqDepth = 0;
        while (dlqDepth < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            dlqDepth = await probeCh.MessageCountAsync($"{queueName}-deadletter");
        }

        Assert.Equal(1u, dlqDepth);

        await hostedService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Consumer_WithDeadLetterQueueOfAnotherType_AdoptsItAndKeepsConsuming()
    {
        // A dead-letter queue that already exists with a different x-queue-type (a classic one created by an
        // earlier version meeting QueueType = Quorum, or the reverse) must be adopted with a warning, like the
        // main queue — not close the consumer channel and leave the consumer in a recovery loop forever.
        TestConsumer.Reset();
        EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Reset();

        var queueName = $"test-dlq-type-{Guid.NewGuid():N}";
        var dlqName = $"{queueName}-deadletter";

        using (var adminConn = await _fixture.CreateDirectConnectionAsync())
        using (var adminCh = await adminConn.CreateChannelAsync())
        {
            await adminCh.QueueDeclareAsync(dlqName, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["x-queue-type"] = "classic" });
        }

        var sp = _fixture.BuildServiceProviderWithConsumers(settings =>
        {
            settings.AddConsumer<TestConsumer>(queueName, cfg =>
            {
                cfg.AutoGenerate = true;
                cfg.Timeout = TimeSpan.FromSeconds(10);
                cfg.ConfigureAutoGenerate(ag =>
                {
                    ag.GenerateExchange = false;
                    ag.GenerateDeadletterQueue = true;
                    ag.DurableQueue = true;
                    ag.AutoDeleteQueue = false;
                    ag.QueueType = QueueType.Quorum; // DLQ would follow as quorum, but it already exists as classic
                });
            });
        });

        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();
        await publisher.PublishAsync(new TestEvent { Id = "dlq-type", Message = "adopted" }, queueName);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (TestConsumer.ReceivedMessages.Count < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        var logs = EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Dump();

        Assert.True(TestConsumer.ReceivedMessages.Count == 1, $"Consumer did not consume with a mismatched DLQ.\nLOGS:\n{logs}");
        Assert.Contains($"Queue '{dlqName}' already exists with arguments that differ", logs);
        Assert.Equal("classic", await _fixture.GetQueueTypeAsync(dlqName));
        Assert.Equal("quorum", await _fixture.GetQueueTypeAsync(queueName));

        await hostedService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Consumer_WithZeroTimeout_MeansNoTimeout_AndProcessesNormally()
    {
        // Timeout = TimeSpan.Zero is documented as "no handler timeout" like InfiniteTimeSpan. A CancellationTokenSource
        // built from Zero would cancel every attempt immediately, so the consumer must normalise it.
        TestConsumer.Reset();

        var queueName = $"test-zero-timeout-{Guid.NewGuid():N}";

        var sp = _fixture.BuildServiceProviderWithConsumers(settings =>
        {
            settings.AddConsumer<TestConsumer>(queueName, cfg =>
            {
                cfg.AutoGenerate = true;
                cfg.Timeout = TimeSpan.Zero;
                cfg.ConfigureAutoGenerate(ag =>
                {
                    ag.GenerateExchange = false;
                    ag.GenerateDeadletterQueue = false;
                    ag.DurableQueue = true;
                    ag.AutoDeleteQueue = true;
                });
            });
        });

        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();
        await publisher.PublishAsync(new TestEvent { Id = "zero", Message = "no-timeout" }, queueName);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (TestConsumer.ReceivedMessages.Count < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.Single(TestConsumer.ReceivedMessages);

        await hostedService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Consumer_WithExplicitClassic_OnExistingQueueDeclaredWithoutType_AdoptsOrMatches()
    {
        // Pinning QueueType = Classic on a consumer whose queue was created earlier without x-queue-type (every
        // queue declared before 8.4.0) must not break it: on a classic-default broker the broker treats the two
        // declarations as equivalent; on a quorum-default broker the existing queue is a quorum queue, the declare
        // is rejected and the consumer adopts it with a warning. Either way it keeps consuming.
        TestConsumer.Reset();
        EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Reset();

        var queueName = $"test-preexisting-{Guid.NewGuid():N}";

        using (var adminConn = await _fixture.CreateDirectConnectionAsync())
        using (var adminCh = await adminConn.CreateChannelAsync())
        {
            await adminCh.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false);
        }

        var sp = _fixture.BuildServiceProviderWithConsumers(settings =>
        {
            settings.AddConsumer<TestConsumer>(queueName, cfg =>
            {
                cfg.AutoGenerate = true;
                cfg.Timeout = TimeSpan.FromSeconds(10);
                cfg.ConfigureAutoGenerate(ag =>
                {
                    ag.GenerateExchange = false;
                    ag.GenerateDeadletterQueue = false;
                    ag.DurableQueue = true;
                    ag.AutoDeleteQueue = false;
                    ag.QueueType = QueueType.Classic;
                });
            });
        });

        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();
        await publisher.PublishAsync(new TestEvent { Id = "pre", Message = "pre-existing" }, queueName);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (TestConsumer.ReceivedMessages.Count < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        var logs = EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Dump();

        Assert.True(TestConsumer.ReceivedMessages.Count == 1, $"Consumer did not consume from the pre-existing queue.\nLOGS:\n{logs}");

        if (RabbitMqFixture.QuorumByDefault)
        {
            // The pre-existing queue is quorum: the explicit classic declare is a mismatch the consumer adopts.
            Assert.Contains("already exists with arguments that differ", logs);
        }
        else
        {
            Assert.DoesNotContain("already exists with arguments that differ", logs);
        }

        await hostedService.StopAsync(CancellationToken.None);
    }

    [SkippableFact]
    [Trait("Category", "Slow")]
    public async Task Consumer_QuorumQueue_SurvivesBrokerConsumerTimeout_AndGetsRedelivery()
    {
        // A delivery held unacknowledged past the queue's consumer timeout makes the broker intervene: RabbitMQ 3.x
        // and 4.0-4.2 close the channel, 4.3+ quorum queues cancel the consumer and leave the channel open. In both
        // cases the delivery is returned to the queue. The library must come back consuming (channel-shutdown
        // recovery or basic.cancel re-subscribe) and receive the message again as a redelivery. The queue is
        // declared quorum explicitly because 4.x classic queues never evaluate consumer timeouts.
        //
        // The handler holds the delivery far longer than the broker timeout and ignores cancellation, so the
        // library's own watchdog (Timeout + 30s) is kept above the broker timeout: Args pins x-consumer-timeout to
        // the 60s minimum while Timeout is 120s. Expect roughly 60-150s of waiting.
        //
        // Not part of CI: whether the broker fires the quorum consumer timeout at all turned out to depend on the
        // broker, not on the library — on 4.3.2 and 4.3.7 Testcontainers the cancel arrived at exactly 60s in some
        // runs and never within 200s in others, with identical queue arguments. The deterministic coverage of the
        // same library path (basic.cancel → re-subscribe) is Consumer_ResubscribesAfterBrokerCancelsConsumer. Run
        // this one by hand against the broker version you deploy to, repeating it if it fails on the first try.
        Skip.IfNot(Environment.GetEnvironmentVariable("RABBITFLOW_BROKER_TIMEOUT_TESTS") == "1", "Set RABBITFLOW_BROKER_TIMEOUT_TESTS=1 to run the broker-dependent consumer timeout test (minutes, not deterministic across broker builds).");

        StuckConsumer.Reset();
        StuckConsumer.HoldFor = TimeSpan.FromMinutes(5);
        EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Reset();

        var queueName = $"test-qq-timeout-{Guid.NewGuid():N}";

        var sp = _fixture.BuildServiceProviderWithConsumers(settings =>
        {
            settings.AddConsumer<StuckConsumer>(queueName, cfg =>
            {
                cfg.AutoGenerate = true;
                cfg.PrefetchCount = 1;
                cfg.Timeout = TimeSpan.FromSeconds(120);
                cfg.ConfigureAutoGenerate(ag =>
                {
                    ag.GenerateExchange = false;
                    ag.GenerateDeadletterQueue = true;
                    ag.DurableQueue = true;
                    ag.AutoDeleteQueue = false;
                    ag.Args = new Dictionary<string, object?>
                    {
                        ["x-queue-type"] = "quorum",
                        ["x-consumer-timeout"] = 60_000L
                    };
                });
            });
        });

        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();
        await publisher.PublishAsync(new TestEvent { Id = "stuck", Message = "held-past-consumer-timeout" }, queueName);

        // First delivery reaches the handler, which then holds it.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (StuckConsumer.Deliveries.Count < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert.Single(StuckConsumer.Deliveries);

        // Act/Assert - the broker's consumer timeout fires (60s + its check interval); the library comes back and
        // the same message is delivered again, flagged as redelivered. Observed on 4.3.7 / 4.4.0: cancel at exactly
        // 60s, redelivery ~90s later (the returned delivery waits for the queue's next evaluation).
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(200);
        while (StuckConsumer.Deliveries.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
        }

        uint consumers;
        using (var probeConn = await _fixture.CreateDirectConnectionAsync())
        using (var probeCh = await probeConn.CreateChannelAsync())
        {
            consumers = (await probeCh.QueueDeclarePassiveAsync(queueName)).ConsumerCount;
        }

        var deliveries = StuckConsumer.Deliveries.OrderBy(d => d.ReceivedUtc).ToList();

        // Timeline for the record: which path the broker took (basic.cancel vs channel close) and how long it waited.
        _output.WriteLine($"Deliveries: {string.Join(", ", deliveries.Select(d => $"{d.ReceivedUtc:HH:mm:ss.fff} redelivered={d.Redelivered}"))}");
        _output.WriteLine(EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Dump());

        Assert.True(
            deliveries.Count >= 2 && deliveries[1].Redelivered && consumers == 1,
            $"Consumer did not come back after the broker consumer timeout. Deliveries={deliveries.Count}, redelivered={(deliveries.Count > 1 ? deliveries[1].Redelivered : (bool?)null)}, consumers={consumers}.\nLOGS:\n{EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Dump()}");

        var stopStarted = DateTime.UtcNow;

        await hostedService.StopAsync(CancellationToken.None);

        _output.WriteLine($"StopAsync took {(DateTime.UtcNow - stopStarted).TotalSeconds:F1}s");
    }

    [Fact]
    public async Task Consumer_HandlerFailure_IsLoggedAtError_WithMessageIdAndException()
    {
        // A dead-lettered message must leave a log line that names the message, the consumer and the exception:
        // without it, a failing handler is invisible unless the handler logs by itself (the envelope in the DLQ is
        // not a log).
        AlwaysFailConsumer.Reset();
        EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Reset();

        var queueName = $"test-faillog-{Guid.NewGuid():N}";

        var sp = _fixture.BuildServiceProviderWithConsumers(settings =>
        {
            settings.AddConsumer<AlwaysFailConsumer>(queueName, cfg =>
            {
                cfg.AutoGenerate = true;
                cfg.Timeout = TimeSpan.FromSeconds(10);
                cfg.ConfigureAutoGenerate(ag =>
                {
                    ag.GenerateExchange = false;
                    ag.GenerateDeadletterQueue = true;
                    ag.DurableQueue = true;
                    ag.AutoDeleteQueue = false;
                });
            });
        });

        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();
        var result = await publisher.PublishAsync(new TestEvent { Id = "boom", Message = "fails" }, queueName, messageId: "msg-fail-1", correlationId: "corr-fail-1");
        Assert.True(result.Success);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Lines.Any(l => l.Contains("failed for AlwaysFailConsumer")) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        var line = EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Lines.FirstOrDefault(l => l.Contains("failed for AlwaysFailConsumer"));

        Assert.True(line != null, $"No terminal failure log line.\nLOGS:\n{EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Dump()}");
        Assert.Contains("Error:", line);
        Assert.Contains("msg-fail-1", line);
        Assert.Contains("(handler_error)", line);
        Assert.Contains("dead-lettering it", line);
        Assert.Contains("InvalidOperationException", line);
        Assert.Contains("CorrelationId=corr-fail-1", line);

        await hostedService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Consumer_HandlerLogs_CarryTheMessageScope()
    {
        // The consumer opens a log scope with the queue, consumer and message identity around the handler, so a
        // handler's own log lines can be tied to the message without a tracer.
        LoggingConsumer.Reset();
        EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Reset();

        var queueName = $"test-scope-{Guid.NewGuid():N}";

        var sp = _fixture.BuildServiceProviderWithConsumers(settings =>
        {
            settings.AddConsumer<LoggingConsumer>(queueName, cfg =>
            {
                cfg.AutoGenerate = true;
                cfg.Timeout = TimeSpan.FromSeconds(10);
                cfg.ConfigureAutoGenerate(ag =>
                {
                    ag.GenerateExchange = false;
                    ag.GenerateDeadletterQueue = false;
                    ag.DurableQueue = true;
                    ag.AutoDeleteQueue = true;
                });
            });
        });

        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();
        await publisher.PublishAsync(new TestEvent { Id = "scoped", Message = "scoped" }, queueName, messageId: "msg-scope-1", correlationId: "corr-scope-1");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (LoggingConsumer.ReceivedMessages.Count < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        var line = EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Lines.FirstOrDefault(l => l.Contains("handler-log scoped"));

        Assert.True(line != null, $"Handler log line not captured.\nLOGS:\n{EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Dump()}");
        Assert.Contains($"Queue={queueName}", line);
        Assert.Contains("Consumer=LoggingConsumer", line);
        Assert.Contains("MessageId=msg-scope-1", line);
        Assert.Contains("CorrelationId=corr-scope-1", line);

        await hostedService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Consumer_QuorumQueue_GivesUpOnAMessageTheBrokerKeepsReturning()
    {
        // A quorum queue counts every time the broker takes a delivery back (x-delivery-count). The library gives
        // up on a message once that count reaches MaxBrokerDeliveries: the handler is not invoked and the message
        // is dead-lettered as a permanent failure (RabbitFlowPoisonMessageException), so it stops going back to the
        // head of the queue. The broker is made to return the delivery by dropping every connection while the
        // handler holds it; the library's recovery then re-subscribes and the broker redelivers.
        TokenHonoringHoldConsumer.Reset();
        EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Reset();

        var queueName = $"test-poison-{Guid.NewGuid():N}";
        var dlqName = $"{queueName}-deadletter";

        var sp = _fixture.BuildServiceProviderWithConsumers(settings =>
        {
            settings.AddConsumer<TokenHonoringHoldConsumer>(queueName, cfg =>
            {
                cfg.AutoGenerate = true;
                cfg.PrefetchCount = 4; // the previous, still-cancelling attempt must not hold the slot of the redelivery
                cfg.Timeout = TimeSpan.FromSeconds(5);
                cfg.MaxBrokerDeliveries = 2;
                cfg.ConfigureAutoGenerate(ag =>
                {
                    ag.GenerateExchange = false;
                    ag.GenerateDeadletterQueue = true;
                    ag.DurableQueue = true;
                    ag.AutoDeleteQueue = false;
                    ag.QueueType = QueueType.Quorum;
                });
            });
        }, host => host.NetworkRecoveryInterval = TimeSpan.FromSeconds(1));

        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();
        await publisher.PublishAsync(new TestEvent { Id = "poison", Message = "always-returned" }, queueName);

        // Deliveries 1 and 2 (x-delivery-count 0 and 1) reach the handler; after each one the connection is dropped
        // while the delivery is unacknowledged, so the broker returns it with the count incremented.
        for (var expected = 1; expected <= 2; expected++)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (TokenHonoringHoldConsumer.Deliveries.Count < expected && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            Assert.True(TokenHonoringHoldConsumer.Deliveries.Count == expected,
                $"Expected delivery #{expected}. Deliveries={TokenHonoringHoldConsumer.Deliveries.Count}.\nLOGS:\n{EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Dump()}");

            await Task.Delay(300);

            Assert.Equal(0, await _fixture.CloseAllConnectionsAsync());
        }

        // Delivery 3 arrives with x-delivery-count = 2 = MaxBrokerDeliveries: dead-lettered, handler not called.
        BasicGetResult? dlqResult = null;
        var dlqDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        using (var probeConn = await _fixture.CreateDirectConnectionAsync())
        using (var probeCh = await probeConn.CreateChannelAsync())
        {
            while (dlqResult == null && DateTime.UtcNow < dlqDeadline)
            {
                dlqResult = await probeCh.BasicGetAsync(dlqName, autoAck: true);
                if (dlqResult == null) await Task.Delay(200);
            }
        }

        var logs = EasyRabbitFlow.Tests.Helpers.MemoryLogSink.Dump();

        Assert.True(dlqResult != null, $"Poison message never reached the DLQ. Deliveries={TokenHonoringHoldConsumer.Deliveries.Count}.\nQUEUES:\n{await _fixture.DumpQueuesAsync(queueName)}\nLOGS:\n{logs}");

        var envelope = JsonSerializer.Deserialize<DeadLetterEnvelope>(dlqResult!.Body.Span, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.NotNull(envelope);
        Assert.Equal(nameof(EasyRabbitFlow.Exceptions.RabbitFlowPoisonMessageException), envelope!.ExceptionType);
        Assert.False(envelope.IsTransient);

        // The handler saw exactly the two deliveries below the limit, with the broker's count on the second.
        var deliveries = TokenHonoringHoldConsumer.Deliveries.ToList();
        Assert.Equal(2, deliveries.Count);
        Assert.Contains(deliveries, d => d.Redelivered && d.DeliveryCount == 1);

        await hostedService.StopAsync(CancellationToken.None);
    }

    [SkippableFact]
    [Trait("Category", "Slow")]
    public async Task Consumer_StopAsync_IsBounded_WhenADeliveryWaitsForAPrefetchSlotHeldByAStuckHandler()
    {
        // PrefetchCount = 1, a handler that ignores cancellation holds the only slot, and a second delivery is
        // waiting for it on the client's consumer dispatcher. Closing the channel waits for that dispatcher, so
        // without cancelling the wait StopAsync would hang until the watchdog abandons the stuck handler
        // (Timeout + 30s). Expected: the bounded 30s drain, then shutdown.
        Skip.IfNot(Environment.GetEnvironmentVariable("RABBITFLOW_SLOW_TESTS") == "1", "Set RABBITFLOW_SLOW_TESTS=1 to run the minutes-long shutdown tests.");

        StuckConsumer.Reset();
        StuckConsumer.HoldFor = TimeSpan.FromMinutes(5);

        var queueName = $"test-stop-bounded-{Guid.NewGuid():N}";

        var sp = _fixture.BuildServiceProviderWithConsumers(settings =>
        {
            settings.AddConsumer<StuckConsumer>(queueName, cfg =>
            {
                cfg.AutoGenerate = true;
                cfg.PrefetchCount = 1;
                cfg.Timeout = TimeSpan.FromSeconds(120); // watchdog at 150s, well past the 30s drain
                cfg.ConfigureAutoGenerate(ag =>
                {
                    ag.GenerateExchange = false;
                    ag.GenerateDeadletterQueue = false;
                    ag.DurableQueue = true;
                    ag.AutoDeleteQueue = false;
                });
            });
        });

        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();
        await publisher.PublishAsync(new TestEvent { Id = "first", Message = "holds-the-slot" }, queueName);
        await publisher.PublishAsync(new TestEvent { Id = "second", Message = "waits-for-the-slot" }, queueName);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (StuckConsumer.Deliveries.Count < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert.Single(StuckConsumer.Deliveries);

        // The second message has been delivered to the channel (prefetch 1 = one unacked + the next one buffered
        // by the client) and is parked on the semaphore.
        await Task.Delay(1000);

        var stopStarted = DateTime.UtcNow;

        await hostedService.StopAsync(CancellationToken.None);

        var took = DateTime.UtcNow - stopStarted;

        _output.WriteLine($"StopAsync took {took.TotalSeconds:F1}s");

        Assert.True(took < TimeSpan.FromSeconds(45), $"StopAsync took {took.TotalSeconds:F1}s; expected the bounded 30s drain, not the handler watchdog.");
    }
}
