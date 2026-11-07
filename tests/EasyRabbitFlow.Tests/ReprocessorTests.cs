using System.Text;
using System.Text.Json;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using EasyRabbitFlow.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace EasyRabbitFlow.Tests;

[Collection("RabbitMq")]
public class ReprocessorTests
{
    private readonly RabbitMqFixture _fixture;

    public ReprocessorTests(RabbitMqFixture fixture)
    {
        _fixture = fixture;
    }

    // The tests publish to the DLQ without publisher confirms and then run a cycle at once. On a quorum queue
    // the publish is only visible once the Raft log commits it, so a cycle started immediately can find the DLQ
    // empty and park nothing. Waiting for the depth mirrors reality (the reprocessor runs hours later) and keeps
    // the assertions about the cycle itself.
    private static async Task WaitForDepthAsync(IChannel ch, string queue, uint expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (await ch.MessageCountAsync(queue) < expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task RunCycle_Reenqueues_Transients_Parks_TheRest_And_RestoresProperties()
    {
        // Arrange
        var queueName = $"test-reproc-{Guid.NewGuid():N}";
        var dlqName = $"{queueName}-deadletter";
        var parkingName = $"{queueName}-deadletter-parking";

        var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();

        await ch.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false);
        await ch.QueueDeclareAsync(dlqName, durable: true, exclusive: false, autoDelete: false);

        try
        {
            var messageData = JsonSerializer.Deserialize<JsonElement>("""{"id":"r1","message":"replay-me"}""");

            // 1) Transient, attempts below max → must be re-enqueued with restored properties
            var eligible = new DeadLetterEnvelope
            {
                DateUtc = DateTime.UtcNow,
                MessageType = "TestEvent",
                MessageId = "msg-1",
                CorrelationId = "corr-1",
                MessageData = messageData,
                ExceptionType = "DerivedTransientException",
                IsTransient = true,
                ReprocessAttempts = 0,
                Properties = new DeadLetterMessageProperties
                {
                    DeliveryMode = MessageDeliveryMode.Persistent,
                    Type = "TestEvent",
                    AppId = "test-app",
                    Headers = new Dictionary<string, string?>
                    {
                        ["tenant"] = "acme",
                        ["traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"
                    }
                }
            };

            // 2) Transient but exhausted → must be parked
            var exhausted = new DeadLetterEnvelope
            {
                DateUtc = DateTime.UtcNow,
                MessageType = "TestEvent",
                MessageData = messageData,
                ExceptionType = "RabbitFlowTransientException",
                IsTransient = true,
                ReprocessAttempts = 3
            };

            // 3) Permanent → must be parked
            var permanent = new DeadLetterEnvelope
            {
                DateUtc = DateTime.UtcNow,
                MessageType = "TestEvent",
                MessageData = messageData,
                ExceptionType = "InvalidOperationException",
                IsTransient = false,
                ReprocessAttempts = 0
            };

            foreach (var envelope in new[] { eligible, exhausted, permanent })
            {
                await ch.BasicPublishAsync("", dlqName, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, jsonOpts)));
            }

            var factory = new ConnectionFactory
            {
                HostName = _fixture.Host,
                Port = _fixture.Port,
                UserName = _fixture.Username,
                Password = _fixture.Password
            };

            var worker = new DeadLetterReprocessWorker(
                NullLogger.Instance,
                factory,
                jsonOpts,
                consumerName: "ReprocessorTestConsumer",
                queueName: queueName,
                maxAttempts: 3,
                interval: TimeSpan.FromHours(1),
                maxMessagesPerCycle: 100,
                finalAction: DeadLetterFinalAction.Park,
                parkingMessageTtl: null);

            await WaitForDepthAsync(ch, dlqName, 3);

            // Act
            await worker.RunCycleAsync(CancellationToken.None);

            // Assert — DLQ fully drained, one re-enqueued, two parked
            Assert.Equal(0u, await ch.MessageCountAsync(dlqName));
            Assert.Equal(1u, await ch.MessageCountAsync(queueName));
            Assert.Equal(2u, await ch.MessageCountAsync(parkingName));

            var replayed = await ch.BasicGetAsync(queueName, autoAck: true);
            Assert.NotNull(replayed);

            // Identity restored
            Assert.Equal("msg-1", replayed!.BasicProperties.MessageId);
            Assert.Equal("corr-1", replayed.BasicProperties.CorrelationId);

            // AMQP properties restored — persistence survives the replay
            Assert.Equal(DeliveryModes.Persistent, replayed.BasicProperties.DeliveryMode);
            Assert.Equal("TestEvent", replayed.BasicProperties.Type);
            Assert.Equal("test-app", replayed.BasicProperties.AppId);

            // Headers restored + incremented counter
            var headers = replayed.BasicProperties.Headers;
            Assert.NotNull(headers);
            Assert.Equal(1, RabbitFlowHeaders.ReadReprocessAttempts(headers));
            Assert.Equal("acme", ReadHeaderString(headers!, "tenant"));
            Assert.StartsWith("00-0af7651916cd43dd", ReadHeaderString(headers!, "traceparent"));

            // Payload is the raw original message, not the envelope
            var payload = JsonSerializer.Deserialize<JsonElement>(replayed.Body.Span);
            Assert.Equal("r1", payload.GetProperty("id").GetString());

            // Parked messages are intact envelopes; the exhausted one keeps its capped counter
            var parkedAttempts = new List<int>();
            for (var i = 0; i < 2; i++)
            {
                var parked = await ch.BasicGetAsync(parkingName, autoAck: true);
                Assert.NotNull(parked);
                var parkedEnvelope = JsonSerializer.Deserialize<DeadLetterEnvelope>(parked!.Body.Span, jsonOpts);
                Assert.NotNull(parkedEnvelope);
                parkedAttempts.Add(parkedEnvelope!.ReprocessAttempts);
            }
            Assert.Contains(3, parkedAttempts);

            // Second cycle is a no-op: nothing rotates through the DLQ anymore
            await worker.RunCycleAsync(CancellationToken.None);
            Assert.Equal(0u, await ch.MessageCountAsync(dlqName));
        }
        finally
        {
            try { await ch.QueueDeleteAsync(queueName); } catch { }
            try { await ch.QueueDeleteAsync(dlqName); } catch { }
            try { await ch.QueueDeleteAsync(parkingName); } catch { }
        }
    }

    [Fact]
    public async Task RunCycle_Parks_Messages_As_Persistent_Preserving_OriginalProperties()
    {
        // Arrange
        var queueName = $"test-reproc-persist-{Guid.NewGuid():N}";
        var dlqName = $"{queueName}-deadletter";
        var parkingName = $"{queueName}-deadletter-parking";

        var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();

        await ch.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false);
        await ch.QueueDeclareAsync(dlqName, durable: true, exclusive: false, autoDelete: false);

        try
        {
            var messageData = JsonSerializer.Deserialize<JsonElement>("""{"id":"p1"}""");

            // Exhausted transient → parked as the re-serialized envelope (no original properties on the wire)
            var exhausted = new DeadLetterEnvelope
            {
                DateUtc = DateTime.UtcNow,
                MessageType = "TestEvent",
                MessageId = "exh-1",
                CorrelationId = "exh-corr",
                MessageData = messageData,
                ExceptionType = "RabbitFlowTransientException",
                IsTransient = true,
                ReprocessAttempts = 3
            };
            await ch.BasicPublishAsync("", dlqName, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(exhausted, jsonOpts)));

            // Permanent → parked unchanged; its original AMQP properties must be preserved even though it
            // arrived on the DLQ as non-persistent.
            var permanent = new DeadLetterEnvelope
            {
                DateUtc = DateTime.UtcNow,
                MessageType = "TestEvent",
                MessageData = messageData,
                ExceptionType = "InvalidOperationException",
                IsTransient = false,
                ReprocessAttempts = 0
            };
            var permProps = new BasicProperties
            {
                MessageId = "perm-1",
                CorrelationId = "perm-corr",
                DeliveryMode = DeliveryModes.Transient,
                Headers = new Dictionary<string, object?> { ["tenant"] = "acme" }
            };
            await ch.BasicPublishAsync("", dlqName, mandatory: false, basicProperties: permProps,
                body: Encoding.UTF8.GetBytes(JsonSerializer.Serialize(permanent, jsonOpts)));

            var factory = new ConnectionFactory
            {
                HostName = _fixture.Host,
                Port = _fixture.Port,
                UserName = _fixture.Username,
                Password = _fixture.Password
            };

            var worker = new DeadLetterReprocessWorker(
                NullLogger.Instance,
                factory,
                jsonOpts,
                consumerName: "ReprocessorPersistConsumer",
                queueName: queueName,
                maxAttempts: 3,
                interval: TimeSpan.FromHours(1),
                maxMessagesPerCycle: 100,
                finalAction: DeadLetterFinalAction.Park,
                parkingMessageTtl: null);

            await WaitForDepthAsync(ch, dlqName, 2);

            // Act
            await worker.RunCycleAsync(CancellationToken.None);

            // Assert — both failures parked, none re-enqueued
            Assert.Equal(0u, await ch.MessageCountAsync(dlqName));
            Assert.Equal(0u, await ch.MessageCountAsync(queueName));
            Assert.Equal(2u, await ch.MessageCountAsync(parkingName));

            // Bucket the two parked messages by their MessageId rather than relying on drain order
            var parkedById = new Dictionary<string, BasicGetResult>();
            for (var i = 0; i < 2; i++)
            {
                var parked = await ch.BasicGetAsync(parkingName, autoAck: true);
                Assert.NotNull(parked);

                // Every parked message must be persistent so it survives a broker restart
                Assert.Equal(DeliveryModes.Persistent, parked!.BasicProperties.DeliveryMode);

                parkedById[parked.BasicProperties.MessageId!] = parked;
            }

            Assert.True(parkedById.ContainsKey("exh-1"));
            Assert.True(parkedById.ContainsKey("perm-1"));

            // Permanent message was parked unchanged: original identity + headers preserved
            var perm = parkedById["perm-1"];
            Assert.Equal("perm-corr", perm.BasicProperties.CorrelationId);
            Assert.Equal("acme", ReadHeaderString(perm.BasicProperties.Headers!, "tenant"));
        }
        finally
        {
            try { await ch.QueueDeleteAsync(queueName); } catch { }
            try { await ch.QueueDeleteAsync(dlqName); } catch { }
            try { await ch.QueueDeleteAsync(parkingName); } catch { }
        }
    }

    [Fact]
    public async Task RunCycle_Discard_DropsExhaustedAndPermanent_ReenqueuesTransient_AndNeverCreatesParking()
    {
        // Arrange
        var queueName = $"test-reproc-discard-{Guid.NewGuid():N}";
        var dlqName = $"{queueName}-deadletter";
        var parkingName = $"{queueName}-deadletter-parking";

        var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();

        await ch.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false);
        await ch.QueueDeclareAsync(dlqName, durable: true, exclusive: false, autoDelete: false);

        try
        {
            var messageData = JsonSerializer.Deserialize<JsonElement>("""{"id":"d1"}""");

            var eligible = new DeadLetterEnvelope
            {
                DateUtc = DateTime.UtcNow,
                MessageType = "TestEvent",
                MessageData = messageData,
                ExceptionType = "RabbitFlowTransientException",
                IsTransient = true,
                ReprocessAttempts = 0
            };

            var exhausted = new DeadLetterEnvelope
            {
                DateUtc = DateTime.UtcNow,
                MessageType = "TestEvent",
                MessageData = messageData,
                ExceptionType = "RabbitFlowTransientException",
                IsTransient = true,
                ReprocessAttempts = 3
            };

            var permanent = new DeadLetterEnvelope
            {
                DateUtc = DateTime.UtcNow,
                MessageType = "TestEvent",
                MessageData = messageData,
                ExceptionType = "InvalidOperationException",
                IsTransient = false,
                ReprocessAttempts = 0
            };

            foreach (var envelope in new[] { eligible, exhausted, permanent })
            {
                await ch.BasicPublishAsync("", dlqName, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, jsonOpts)));
            }

            var worker = new DeadLetterReprocessWorker(
                NullLogger.Instance,
                Factory(),
                jsonOpts,
                consumerName: "ReprocessorDiscardConsumer",
                queueName: queueName,
                maxAttempts: 3,
                interval: TimeSpan.FromHours(1),
                maxMessagesPerCycle: 100,
                finalAction: DeadLetterFinalAction.Discard,
                parkingMessageTtl: null);

            await WaitForDepthAsync(ch, dlqName, 3);

            // Act
            await worker.RunCycleAsync(CancellationToken.None);

            // Assert — DLQ drained, transient re-enqueued, exhausted + permanent dropped,
            // and the parking queue was never created since nothing needed parking.
            Assert.Equal(0u, await ch.MessageCountAsync(dlqName));
            Assert.Equal(1u, await ch.MessageCountAsync(queueName));
            Assert.False(await QueueExistsAsync(conn, parkingName));
        }
        finally
        {
            try { await ch.QueueDeleteAsync(queueName); } catch { }
            try { await ch.QueueDeleteAsync(dlqName); } catch { }
            try { await ch.QueueDeleteAsync(parkingName); } catch { }
        }
    }

    [Fact]
    public async Task RunCycle_Discard_StillParksMalformed_CreatingParkingOnDemand()
    {
        // Arrange
        var queueName = $"test-reproc-malformed-{Guid.NewGuid():N}";
        var dlqName = $"{queueName}-deadletter";
        var parkingName = $"{queueName}-deadletter-parking";

        var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();

        await ch.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false);
        await ch.QueueDeclareAsync(dlqName, durable: true, exclusive: false, autoDelete: false);

        try
        {
            // Bytes that cannot be deserialized into a DeadLetterEnvelope → classified malformed.
            await ch.BasicPublishAsync("", dlqName, Encoding.UTF8.GetBytes("this-is-not-json"));

            var worker = new DeadLetterReprocessWorker(
                NullLogger.Instance,
                Factory(),
                jsonOpts,
                consumerName: "ReprocessorMalformedConsumer",
                queueName: queueName,
                maxAttempts: 3,
                interval: TimeSpan.FromHours(1),
                maxMessagesPerCycle: 100,
                finalAction: DeadLetterFinalAction.Discard,
                parkingMessageTtl: null);

            await WaitForDepthAsync(ch, dlqName, 1);

            // Act
            await worker.RunCycleAsync(CancellationToken.None);

            // Assert — malformed bytes are never discarded; the parking queue is created on demand for them.
            Assert.Equal(0u, await ch.MessageCountAsync(dlqName));
            Assert.True(await QueueExistsAsync(conn, parkingName));
            Assert.Equal(1u, await ch.MessageCountAsync(parkingName));
        }
        finally
        {
            try { await ch.QueueDeleteAsync(queueName); } catch { }
            try { await ch.QueueDeleteAsync(dlqName); } catch { }
            try { await ch.QueueDeleteAsync(parkingName); } catch { }
        }
    }

    [Fact]
    public async Task RunCycle_AppliesParkingMessageTtl_OnQueueCreation()
    {
        // Arrange
        var queueName = $"test-reproc-ttl-{Guid.NewGuid():N}";
        var dlqName = $"{queueName}-deadletter";
        var parkingName = $"{queueName}-deadletter-parking";

        var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();

        await ch.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false);
        await ch.QueueDeclareAsync(dlqName, durable: true, exclusive: false, autoDelete: false);

        try
        {
            var exhausted = new DeadLetterEnvelope
            {
                DateUtc = DateTime.UtcNow,
                MessageType = "TestEvent",
                MessageData = JsonSerializer.Deserialize<JsonElement>("""{"id":"t1"}"""),
                ExceptionType = "RabbitFlowTransientException",
                IsTransient = true,
                ReprocessAttempts = 3
            };
            await ch.BasicPublishAsync("", dlqName, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(exhausted, jsonOpts)));

            var worker = new DeadLetterReprocessWorker(
                NullLogger.Instance,
                Factory(),
                jsonOpts,
                consumerName: "ReprocessorTtlConsumer",
                queueName: queueName,
                maxAttempts: 3,
                interval: TimeSpan.FromHours(1),
                maxMessagesPerCycle: 100,
                finalAction: DeadLetterFinalAction.Park,
                parkingMessageTtl: TimeSpan.FromSeconds(2));

            await WaitForDepthAsync(ch, dlqName, 1);

            // Act — message is parked into a queue declared with x-message-ttl = 2s
            await worker.RunCycleAsync(CancellationToken.None);

            Assert.Equal(1u, await ch.MessageCountAsync(parkingName));

            // The TTL applies: once it elapses, the broker drops the parked message.
            await Task.Delay(TimeSpan.FromSeconds(4));

            Assert.Equal(0u, await ch.MessageCountAsync(parkingName));
        }
        finally
        {
            try { await ch.QueueDeleteAsync(queueName); } catch { }
            try { await ch.QueueDeleteAsync(dlqName); } catch { }
            try { await ch.QueueDeleteAsync(parkingName); } catch { }
        }
    }

    [Theory]
    [InlineData(QueueType.Classic, "classic")]
    [InlineData(QueueType.Quorum, "quorum")]
    public async Task RunCycle_AppliesParkingQueueType_OnQueueCreation(QueueType parkingQueueType, string expectedType)
    {
        // The parking queue is created on demand with the dead-letter queue's type, whatever the broker default is.
        var queueName = $"test-reproc-ptype-{Guid.NewGuid():N}";
        var dlqName = $"{queueName}-deadletter";
        var parkingName = $"{queueName}-deadletter-parking";

        var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();

        await ch.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false);
        await ch.QueueDeclareAsync(dlqName, durable: true, exclusive: false, autoDelete: false);

        try
        {
            var permanent = new DeadLetterEnvelope
            {
                DateUtc = DateTime.UtcNow,
                MessageType = "TestEvent",
                MessageData = JsonSerializer.Deserialize<JsonElement>("""{"id":"q1"}"""),
                ExceptionType = "InvalidOperationException",
                IsTransient = false,
                ReprocessAttempts = 0
            };
            await ch.BasicPublishAsync("", dlqName, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(permanent, jsonOpts)));

            var worker = new DeadLetterReprocessWorker(
                NullLogger.Instance,
                Factory(),
                jsonOpts,
                consumerName: "ReprocessorParkingTypeConsumer",
                queueName: queueName,
                maxAttempts: 3,
                interval: TimeSpan.FromHours(1),
                maxMessagesPerCycle: 100,
                finalAction: DeadLetterFinalAction.Park,
                parkingMessageTtl: null,
                parkingQueueType: parkingQueueType);

            await WaitForDepthAsync(ch, dlqName, 1);

            await worker.RunCycleAsync(CancellationToken.None);

            Assert.Equal(1u, await ch.MessageCountAsync(parkingName));
            Assert.Equal(expectedType, await _fixture.GetQueueTypeAsync(parkingName));
        }
        finally
        {
            try { await ch.QueueDeleteAsync(queueName); } catch { }
            try { await ch.QueueDeleteAsync(dlqName); } catch { }
            try { await ch.QueueDeleteAsync(parkingName); } catch { }
        }
    }

    [Theory]
    [InlineData("transient")]
    [InlineData("exhausted")]
    [InlineData("permanent")]
    [InlineData("malformed")]
    public async Task RunCycle_UnroutablePublish_PreservesOriginal_AndRetriesNextCycle(string kind)
    {
        var queueName = $"test-reproc-return-{Guid.NewGuid():N}";
        var dlqName = $"{queueName}-deadletter";
        var parkingName = $"{queueName}-deadletter-parking";
        var destination = kind == "transient" ? queueName : parkingName;
        var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var envelope = new DeadLetterEnvelope
        {
            MessageId = "returned-message",
            MessageData = JsonSerializer.Deserialize<JsonElement>("""{"id":"keep-me"}"""),
            IsTransient = kind != "permanent",
            ReprocessAttempts = kind == "exhausted" ? 3 : 0
        };
        var bytes = Encoding.UTF8.GetBytes(kind == "malformed" ? "invalid-json" : JsonSerializer.Serialize(envelope, jsonOpts));

        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await ch.QueueDeclareAsync(dlqName, durable: true, exclusive: false, autoDelete: false);
        await ch.QueueDeclareAsync(destination, durable: true, exclusive: false, autoDelete: false);
        try
        {
            await ch.BasicPublishAsync("", dlqName, false,
                new BasicProperties { MessageId = "original-id", CorrelationId = "original-correlation" }, bytes);
            await WaitForDepthAsync(ch, dlqName, 1);

            var logger = new ReturnedPublishLogger();
            if (kind == "transient")
            {
                await ch.QueueDeleteAsync(destination);
            }
            else
            {
                // Synchronize the administrative deletion with the successful passive-declare log.
                // A separate channel completes deletion before the worker can publish: no timing race.
                logger.AfterParkingProbe = async () => await ch.QueueDeleteAsync(destination,
                    cancellationToken: timeout.Token);
            }

            var worker = new DeadLetterReprocessWorker(logger, Factory(), jsonOpts,
                "ReturnedPublishConsumer", queueName, 3, TimeSpan.FromHours(1), 100,
                DeadLetterFinalAction.Park, null);
            // The synchronous logging barrier runs without xUnit's synchronization context.
            await Task.Run(() => worker.RunCycleAsync(timeout.Token));

            Assert.Contains(logger.Exceptions, ex => ex is PublishException { IsReturn: true });
            await WaitForDepthAsync(ch, dlqName, 1);
            Assert.Equal(1u, await ch.MessageCountAsync(dlqName));
            var retained = await ch.BasicGetAsync(dlqName, autoAck: false);
            Assert.NotNull(retained);
            Assert.Equal(bytes, retained!.Body.ToArray());
            Assert.Equal("original-id", retained.BasicProperties.MessageId);
            Assert.Equal("original-correlation", retained.BasicProperties.CorrelationId);
            await ch.BasicNackAsync(retained.DeliveryTag, multiple: false, requeue: true);

            await ch.QueueDeclareAsync(destination, durable: true, exclusive: false, autoDelete: false);
            await WaitForDepthAsync(ch, dlqName, 1);
            await worker.RunCycleAsync(timeout.Token);

            Assert.Equal(0u, await ch.MessageCountAsync(dlqName));
            Assert.Equal(1u, await ch.MessageCountAsync(destination));
            var delivered = await ch.BasicGetAsync(destination, autoAck: true);
            Assert.NotNull(delivered);
            if (kind == "transient")
            {
                Assert.Equal(1, delivered!.BasicProperties.Headers![RabbitFlowHeaders.ReprocessAttempts]);
                Assert.Equal(envelope.MessageData!.Value.GetRawText(), Encoding.UTF8.GetString(delivered.Body.Span));
            }
            else
            {
                Assert.Equal(bytes, delivered!.Body.ToArray());
            }
        }
        finally
        {
            await ch.QueueDeleteAsync(dlqName);
            await ch.QueueDeleteAsync(destination);
        }
    }

    private sealed class ReturnedPublishLogger : ILogger
    {
        public Func<Task>? AfterParkingProbe { get; set; }
        public List<Exception> Exceptions { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception != null) Exceptions.Add(exception);
            if (formatter(state, exception).Contains("already exists; using it as-is"))
            {
                var callback = AfterParkingProbe;
                AfterParkingProbe = null;
                // ILogger is synchronous; wait for the independent administrative channel so
                // deletion is complete before returning control to the publisher.
                callback?.Invoke().GetAwaiter().GetResult();
            }
        }
    }

    private ConnectionFactory Factory() => new ConnectionFactory
    {
        HostName = _fixture.Host,
        Port = _fixture.Port,
        UserName = _fixture.Username,
        Password = _fixture.Password
    };

    private static async Task<bool> QueueExistsAsync(IConnection conn, string queueName)
    {
        using var probe = await conn.CreateChannelAsync();

        try
        {
            await probe.QueueDeclarePassiveAsync(queueName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadHeaderString(IDictionary<string, object?> headers, string key)
    {
        if (!headers.TryGetValue(key, out var raw) || raw == null)
        {
            return null;
        }

        return raw switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string text => text,
            _ => raw.ToString()
        };
    }
}
