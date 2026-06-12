using System.Text;
using System.Text.Json;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Tests.Fixtures;
using EasyRabbitFlow.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace EasyRabbitFlow.Tests;

[Collection("RabbitMq")]
public class StateTests
{
    private readonly RabbitMqFixture _fixture;

    public StateTests(RabbitMqFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task IsEmptyQueueAsync_EmptyQueue_ReturnsTrue()
    {
        // Arrange
        var queueName = $"test-state-empty-{Guid.NewGuid():N}";
        var sp = _fixture.BuildServiceProvider();

        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();
        await ch.QueueDeclareAsync(queueName, durable: false, exclusive: false, autoDelete: true);

        var state = sp.GetRequiredService<IRabbitFlowState>();

        // Act
        var isEmpty = await state.IsEmptyQueueAsync(queueName);

        // Assert
        Assert.True(isEmpty);
    }

    [Fact]
    public async Task IsEmptyQueueAsync_QueueWithMessages_ReturnsFalse()
    {
        // Arrange
        var queueName = $"test-state-notempty-{Guid.NewGuid():N}";
        var sp = _fixture.BuildServiceProvider();

        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();
        await ch.QueueDeclareAsync(queueName, durable: false, exclusive: false, autoDelete: true);

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new TestEvent { Id = "1" }));
        await ch.BasicPublishAsync("", queueName, body);
        await Task.Delay(200);

        var state = sp.GetRequiredService<IRabbitFlowState>();

        // Act
        var isEmpty = await state.IsEmptyQueueAsync(queueName);

        // Assert
        Assert.False(isEmpty);
    }

    [Fact]
    public async Task GetQueueLengthAsync_ReturnsCorrectCount()
    {
        // Arrange
        var queueName = $"test-state-length-{Guid.NewGuid():N}";
        var sp = _fixture.BuildServiceProvider();

        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();
        await ch.QueueDeclareAsync(queueName, durable: false, exclusive: false, autoDelete: true);

        // Publish 3 messages
        for (int i = 0; i < 3; i++)
        {
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new TestEvent { Id = i.ToString() }));
            await ch.BasicPublishAsync("", queueName, body);
        }
        await Task.Delay(200);

        var state = sp.GetRequiredService<IRabbitFlowState>();

        // Act
        var length = await state.GetQueueLengthAsync(queueName);

        // Assert
        Assert.Equal(3u, length);
    }

    [Fact]
    public async Task GetQueueStateAsync_ReturnsFullSnapshot()
    {
        // Arrange
        var queueName = $"test-state-snapshot-{Guid.NewGuid():N}";
        var sp = _fixture.BuildServiceProvider();

        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();
        await ch.QueueDeclareAsync(queueName, durable: false, exclusive: false, autoDelete: true);

        for (int i = 0; i < 2; i++)
        {
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new TestEvent { Id = i.ToString() }));
            await ch.BasicPublishAsync("", queueName, body);
        }
        await Task.Delay(200);

        var state = sp.GetRequiredService<IRabbitFlowState>();

        // Act
        var snapshot = await state.GetQueueStateAsync(queueName);

        // Assert
        Assert.True(snapshot.Exists);
        Assert.Equal(queueName, snapshot.QueueName);
        Assert.Equal(2u, snapshot.MessageCount);
        Assert.Equal(0u, snapshot.ConsumerCount);
        Assert.False(snapshot.IsEmpty);
        Assert.False(snapshot.HasConsumers);
    }

    [Fact]
    public async Task GetQueueStateAsync_MissingQueue_ReportsNotExists_WithoutThrowing()
    {
        // Arrange
        var sp = _fixture.BuildServiceProvider();
        var state = sp.GetRequiredService<IRabbitFlowState>();

        // Act
        var snapshot = await state.GetQueueStateAsync($"missing-{Guid.NewGuid():N}");

        // Assert
        Assert.False(snapshot.Exists);
        Assert.Equal(0u, snapshot.MessageCount);
        Assert.Equal(0u, snapshot.ConsumerCount);
        Assert.False(snapshot.IsEmpty);
        Assert.False(snapshot.HasConsumers);
    }

    [Fact]
    public async Task GetQueuesStateAsync_MixedQueues_PreservesOrder_And_SurvivesMissing()
    {
        // Arrange
        var existingQueue = $"test-state-multi-{Guid.NewGuid():N}";
        var missingQueue = $"missing-{Guid.NewGuid():N}";
        var sp = _fixture.BuildServiceProvider();

        using var conn = await _fixture.CreateDirectConnectionAsync();
        using var ch = await conn.CreateChannelAsync();
        await ch.QueueDeclareAsync(existingQueue, durable: false, exclusive: false, autoDelete: true);

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new TestEvent { Id = "1" }));
        await ch.BasicPublishAsync("", existingQueue, body);
        await Task.Delay(200);

        var state = sp.GetRequiredService<IRabbitFlowState>();

        // Act – the missing queue comes first to prove a 404 doesn't break the rest
        var snapshots = await state.GetQueuesStateAsync(new[] { missingQueue, existingQueue });

        // Assert
        Assert.Equal(2, snapshots.Count);

        Assert.Equal(missingQueue, snapshots[0].QueueName);
        Assert.False(snapshots[0].Exists);

        Assert.Equal(existingQueue, snapshots[1].QueueName);
        Assert.True(snapshots[1].Exists);
        Assert.Equal(1u, snapshots[1].MessageCount);
    }
}
