using System.Text;
using System.Text.Json;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using EasyRabbitFlow.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;

namespace EasyRabbitFlow.Tests;

[Collection("RabbitMq")]
public class ReprocessorTests
{
    private readonly RabbitMqFixture _fixture;

    public ReprocessorTests(RabbitMqFixture fixture)
    {
        _fixture = fixture;
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

        await ch.QueueDeclareAsync(queueName, durable: false, exclusive: false, autoDelete: false);
        await ch.QueueDeclareAsync(dlqName, durable: false, exclusive: false, autoDelete: false);

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
                maxMessagesPerCycle: 100);

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
