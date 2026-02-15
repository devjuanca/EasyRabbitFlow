using System.Text;
using System.Text.Json;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Tests.Fixtures;
using EasyRabbitFlow.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace EasyRabbitFlow.Tests;

[Collection("RabbitMq")]
public class PurgerTests
{
    private readonly RabbitMqFixture _fixture;

    public PurgerTests(RabbitMqFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PurgeMessagesAsync_RemovesAllMessages()
    {
        // Arrange
        var queueName = $"test-purge-{Guid.NewGuid():N}";
        var sp = _fixture.BuildServiceProvider();

        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();
        await ch.QueueDeclareAsync(queueName, durable: false, exclusive: false, autoDelete: true);

        // Publish 5 messages
        for (int i = 0; i < 5; i++)
        {
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new TestEvent { Id = i.ToString() }));
            await ch.BasicPublishAsync("", queueName, body);
        }
        await Task.Delay(200);

        var beforeCount = await ch.MessageCountAsync(queueName);
        Assert.Equal(5u, beforeCount);

        var purger = sp.GetRequiredService<IRabbitFlowPurger>();

        // Act
        await purger.PurgeMessagesAsync(queueName);

        // Assert
        await Task.Delay(200);
        var afterCount = await ch.MessageCountAsync(queueName);
        Assert.Equal(0u, afterCount);
    }

    [Fact]
    public async Task PurgeMessagesAsync_MultipleQueues_PurgesAll()
    {
        // Arrange
        var queue1 = $"test-purge-multi1-{Guid.NewGuid():N}";
        var queue2 = $"test-purge-multi2-{Guid.NewGuid():N}";
        var sp = _fixture.BuildServiceProvider();

        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();
        await ch.QueueDeclareAsync(queue1, durable: false, exclusive: false, autoDelete: true);
        await ch.QueueDeclareAsync(queue2, durable: false, exclusive: false, autoDelete: true);

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new TestEvent { Id = "1" }));
        await ch.BasicPublishAsync("", queue1, body);
        await ch.BasicPublishAsync("", queue2, body);
        await Task.Delay(200);

        var purger = sp.GetRequiredService<IRabbitFlowPurger>();

        // Act
        await purger.PurgeMessagesAsync(new[] { queue1, queue2 });

        // Assert
        await Task.Delay(200);
        Assert.Equal(0u, await ch.MessageCountAsync(queue1));
        Assert.Equal(0u, await ch.MessageCountAsync(queue2));
    }

    [Fact]
    public async Task PurgeMessagesAsync_EmptyQueueName_ThrowsArgumentException()
    {
        var sp = _fixture.BuildServiceProvider();
        var purger = sp.GetRequiredService<IRabbitFlowPurger>();

        await Assert.ThrowsAsync<ArgumentException>(() => purger.PurgeMessagesAsync(""));
    }

    [Fact]
    public async Task PurgeMessagesAsync_NullEnumerable_ThrowsArgumentNullException()
    {
        var sp = _fixture.BuildServiceProvider();
        var purger = sp.GetRequiredService<IRabbitFlowPurger>();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            purger.PurgeMessagesAsync((IEnumerable<string>)null!));
    }
}
