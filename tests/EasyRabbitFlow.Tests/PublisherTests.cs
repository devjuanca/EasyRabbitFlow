using System.Text;
using System.Text.Json;
using System.Reflection;
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
        await ch.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: true);

        var evt = new TestEvent { Id = "1", Message = "hello" };

        // Act
        var result = await publisher.PublishAsync(evt, queueName);

        // Assert
        Assert.True(result.Success);

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
        await ch.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: true);
        await ch.QueueBindAsync(queueName, exchangeName, routingKey);

        var evt = new TestEvent { Id = "2", Message = "routed" };

        // Act
        var result = await publisher.PublishAsync(evt, exchangeName, routingKey: routingKey);

        // Assert
        Assert.True(result.Success);

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
        await ch.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: true);

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

    [Fact]
    public async Task DisposeAsync_DrainsConfirmChannelPool_AndIsIdempotent()
    {
        // Arrange
        var queueName = $"test-dispose-{Guid.NewGuid():N}";
        await using var sp = (ServiceProvider)_fixture.BuildServiceProvider(settings =>
        {
            settings.ConfigurePublisher(options => options.MaxPooledChannels = 1);
        });

        var publisherContract = sp.GetRequiredService<IRabbitFlowPublisher>();
        var publisher = Assert.IsType<RabbitFlowPublisher>(publisherContract);

        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();
        await ch.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: true);

        // Act
        var result = await publisherContract.PublishAsync(new TestEvent { Id = "dispose", Message = "before-dispose" }, queueName);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(1, GetPooledChannelCount(publisher));

        await publisher.DisposeAsync();
        await publisher.DisposeAsync();

        Assert.Equal(0, GetPooledChannelCount(publisher));

        var afterDispose = await publisherContract.PublishAsync(new TestEvent { Id = "dispose", Message = "after-dispose" }, queueName);

        Assert.False(afterDispose.Success);
        Assert.IsType<ObjectDisposedException>(afterDispose.Error);
    }

    [Fact]
    public async Task Dispose_Synchronous_ViaServiceProvider_DoesNotThrow()
    {
        // Arrange
        var queueName = $"test-sync-dispose-{Guid.NewGuid():N}";
        var sp = (ServiceProvider)_fixture.BuildServiceProvider();

        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();

        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();
        await ch.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: true);

        var result = await publisher.PublishAsync(new TestEvent { Id = "sync-dispose", Message = "before-dispose" }, queueName);
        Assert.True(result.Success);

        // Act: synchronous container disposal must not throw (RabbitFlowPublisher bridges IDisposable).
        sp.Dispose();

        // Assert
        var afterDispose = await publisher.PublishAsync(new TestEvent { Id = "sync-dispose", Message = "after-dispose" }, queueName);

        Assert.False(afterDispose.Success);
        Assert.IsType<ObjectDisposedException>(afterDispose.Error);
    }

    private static int GetPooledChannelCount(RabbitFlowPublisher publisher)
    {
        var field = typeof(RabbitFlowPublisher).GetField("pooledChannelCount", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        return (int)field.GetValue(publisher)!;
    }
}
