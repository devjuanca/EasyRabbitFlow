using System.Text;
using System.Text.Json;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Tests.Fixtures;
using EasyRabbitFlow.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace EasyRabbitFlow.Tests;

[Collection("RabbitMq")]
public class PublisherTests
{
    private readonly RabbitMqFixture _fixture;

    public PublisherTests(RabbitMqFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PublishAsync_ToQueue_MessageArrivesInQueue()
    {
        // Arrange
        var queueName = $"test-publish-{Guid.NewGuid():N}";
        var sp = _fixture.BuildServiceProvider();
        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();

        // Pre-declare queue
        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();
        await ch.QueueDeclareAsync(queueName, durable: false, exclusive: false, autoDelete: true);

        var evt = new TestEvent { Id = "1", Message = "hello" };

        // Act
        var result = await publisher.PublishAsync(evt, queueName);

        // Assert
        Assert.True(result);

        var messageCount = await ch.MessageCountAsync(queueName);
        Assert.True(messageCount >= 1, $"Expected at least 1 message in queue, found {messageCount}");
    }

    [Fact]
    public async Task PublishAsync_ToExchange_MessageRouted()
    {
        // Arrange
        var exchangeName = $"test-ex-{Guid.NewGuid():N}";
        var queueName = $"test-q-{Guid.NewGuid():N}";
        var routingKey = "test-rk";

        var sp = _fixture.BuildServiceProvider();
        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();

        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();
        await ch.ExchangeDeclareAsync(exchangeName, "direct", durable: false, autoDelete: true);
        await ch.QueueDeclareAsync(queueName, durable: false, exclusive: false, autoDelete: true);
        await ch.QueueBindAsync(queueName, exchangeName, routingKey);

        var evt = new TestEvent { Id = "2", Message = "routed" };

        // Act
        var result = await publisher.PublishAsync(evt, exchangeName, routingKey: routingKey);

        // Assert
        Assert.True(result);

        await Task.Delay(200); // small delay for message to be routed
        var messageCount = await ch.MessageCountAsync(queueName);
        Assert.True(messageCount >= 1, $"Expected at least 1 message in queue after routing, found {messageCount}");
    }

    [Fact]
    public async Task PublishAsync_NullEvent_ThrowsArgumentNullException()
    {
        var sp = _fixture.BuildServiceProvider();
        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            publisher.PublishAsync<TestEvent>(null!, "some-queue"));
    }

    [Fact]
    public async Task PublishAsync_MessageContent_IsCorrectJson()
    {
        // Arrange
        var queueName = $"test-content-{Guid.NewGuid():N}";
        var sp = _fixture.BuildServiceProvider();
        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();

        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();
        await ch.QueueDeclareAsync(queueName, durable: false, exclusive: false, autoDelete: true);

        var evt = new TestEvent { Id = "42", Message = "content-check" };

        // Act
        await publisher.PublishAsync(evt, queueName);

        // Assert - fetch the message and verify content
        await Task.Delay(200);
        var getResult = await ch.BasicGetAsync(queueName, autoAck: true);
        Assert.NotNull(getResult);

        var json = Encoding.UTF8.GetString(getResult.Body.ToArray());
        var deserialized = JsonSerializer.Deserialize<TestEvent>(json, JsonSerializerOptions.Web);
        Assert.NotNull(deserialized);
        Assert.Equal("42", deserialized.Id);
        Assert.Equal("content-check", deserialized.Message);
    }
}
