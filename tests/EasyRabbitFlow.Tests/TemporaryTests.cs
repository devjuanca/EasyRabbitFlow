using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using EasyRabbitFlow.Tests.Fixtures;
using EasyRabbitFlow.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace EasyRabbitFlow.Tests;

[Collection("RabbitMq")]
public class TemporaryTests
{
    private readonly RabbitMqFixture _fixture;

    public TemporaryTests(RabbitMqFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RunAsync_ProcessesAllMessages()
    {
        // Arrange
        var sp = _fixture.BuildServiceProvider();
        var temporary = sp.GetRequiredService<IRabbitFlowTemporary>();

        var messages = Enumerable.Range(1, 5)
            .Select(i => new TestEvent { Id = i.ToString(), Message = $"msg-{i}" })
            .ToList();

        var received = new List<TestEvent>();

        // Act
        var processed = await temporary.RunAsync<TestEvent>(
            messages,
            async (msg, ct) =>
            {
                lock (received) { received.Add(msg); }
                await Task.CompletedTask;
            });

        // Assert
        Assert.Equal(5, processed);
        Assert.Equal(5, received.Count);
    }

    [Fact]
    public async Task RunAsync_EmptyList_ReturnsZero()
    {
        var sp = _fixture.BuildServiceProvider();
        var temporary = sp.GetRequiredService<IRabbitFlowTemporary>();

        var processed = await temporary.RunAsync<TestEvent>(
            new List<TestEvent>(),
            async (msg, ct) => await Task.CompletedTask);

        Assert.Equal(0, processed);
    }

    [Fact]
    public async Task RunAsync_CompletionCallbackInvoked()
    {
        // Arrange
        var sp = _fixture.BuildServiceProvider();
        var temporary = sp.GetRequiredService<IRabbitFlowTemporary>();

        var messages = new List<TestEvent>
        {
            new() { Id = "1", Message = "a" },
            new() { Id = "2", Message = "b" }
        };

        int completedCount = 0;
        int errorCount = -1;

        // Act
        await temporary.RunAsync<TestEvent>(
            messages,
            async (msg, ct) => await Task.CompletedTask,
            onCompleted: (p, e) => { completedCount = p; errorCount = e; });

        // Assert
        Assert.Equal(2, completedCount);
        Assert.Equal(0, errorCount);
    }

    [Fact]
    public async Task RunAsync_WithResults_CollectsResults()
    {
        // Arrange
        var sp = _fixture.BuildServiceProvider();
        var temporary = sp.GetRequiredService<IRabbitFlowTemporary>();

        var messages = Enumerable.Range(1, 3)
            .Select(i => new TestEvent { Id = i.ToString(), Message = $"item-{i}" })
            .ToList();

        var collectedResults = new List<string>();

        // Act
        var processed = await temporary.RunAsync<TestEvent, string>(
            messages,
            async (msg, ct) =>
            {
                await Task.CompletedTask;
                return $"processed-{msg.Id}";
            },
            async (count, results) =>
            {
                collectedResults.AddRange(results);
                await Task.CompletedTask;
            });

        // Assert
        Assert.Equal(3, processed);
        Assert.Equal(3, collectedResults.Count);
        Assert.All(collectedResults, r => Assert.StartsWith("processed-", r));
    }

    [Fact]
    public async Task RunAsync_WithOptions_UsesCustomSettings()
    {
        // Arrange
        var sp = _fixture.BuildServiceProvider();
        var temporary = sp.GetRequiredService<IRabbitFlowTemporary>();

        var messages = new List<TestEvent>
        {
            new() { Id = "1", Message = "custom" }
        };

        var options = new RunTemporaryOptions
        {
            PrefetchCount = 2,
            QueuePrefixName = "custom-test"
        };

        var received = new List<TestEvent>();

        // Act
        var processed = await temporary.RunAsync<TestEvent>(
            messages,
            async (msg, ct) =>
            {
                lock (received) { received.Add(msg); }
                await Task.CompletedTask;
            },
            options: options);

        // Assert
        Assert.Equal(1, processed);
        Assert.Single(received);
    }

    [Fact]
    public async Task RunAsync_OnError_InvokedForFailedMessages()
    {
        // Arrange
        var sp = _fixture.BuildServiceProvider();
        var temporary = sp.GetRequiredService<IRabbitFlowTemporary>();

        var messages = Enumerable.Range(1, 3)
            .Select(i => new TestEvent { Id = i.ToString(), Message = $"msg-{i}" })
            .ToList();

        var failedMessages = new List<TestEvent>();

        // Act – message with Id "2" throws
        var processed = await temporary.RunAsync<TestEvent>(
            messages,
            async (msg, ct) =>
            {
                if (msg.Id == "2")
                    throw new InvalidOperationException("Simulated failure");

                await Task.CompletedTask;
            },
            onCompleted: (p, e) => { },
            onError: async (msg, ct) =>
            {
                lock (failedMessages) { failedMessages.Add(msg); }
                await Task.CompletedTask;
            },
            options: new RunTemporaryOptions { PrefetchCount = 1 });

        // Assert
        Assert.Equal(3, processed);
        Assert.Single(failedMessages);
        Assert.Equal("2", failedMessages[0].Id);
    }

    [Fact]
    public async Task RunAsync_OnError_InvokedOnTimeout()
    {
        // Arrange
        var sp = _fixture.BuildServiceProvider();
        var temporary = sp.GetRequiredService<IRabbitFlowTemporary>();

        var messages = new List<TestEvent>
        {
            new() { Id = "slow", Message = "will-timeout" }
        };

        var failedMessages = new List<TestEvent>();

        // Act – handler exceeds the timeout
        var processed = await temporary.RunAsync<TestEvent>(
            messages,
            async (msg, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            },
            onError: async (msg, ct) =>
            {
                lock (failedMessages) { failedMessages.Add(msg); }
                await Task.CompletedTask;
            },
            options: new RunTemporaryOptions
            {
                Timeout = TimeSpan.FromMilliseconds(200),
                PrefetchCount = 1
            });

        // Assert
        Assert.Equal(1, processed);
        Assert.Single(failedMessages);
        Assert.Equal("slow", failedMessages[0].Id);
    }

    [Fact]
    public async Task RunAsync_OnError_ReportsCorrectErrorCount()
    {
        // Arrange
        var sp = _fixture.BuildServiceProvider();
        var temporary = sp.GetRequiredService<IRabbitFlowTemporary>();

        var messages = Enumerable.Range(1, 4)
            .Select(i => new TestEvent { Id = i.ToString(), Message = $"msg-{i}" })
            .ToList();

        int completedTotal = 0;
        int completedErrors = 0;
        var failedMessages = new List<TestEvent>();

        // Act – even-numbered messages fail
        var processed = await temporary.RunAsync<TestEvent>(
            messages,
            async (msg, ct) =>
            {
                if (int.Parse(msg.Id) % 2 == 0)
                    throw new Exception("Even fail");

                await Task.CompletedTask;
            },
            onCompleted: (p, e) => { completedTotal = p; completedErrors = e; },
            onError: async (msg, ct) =>
            {
                lock (failedMessages) 
                { 
                    failedMessages.Add(msg); 
                }
                await Task.CompletedTask;
            },
            options: new RunTemporaryOptions { PrefetchCount = 1 });

        // Assert
        Assert.Equal(4, processed);
        Assert.Equal(4, completedTotal);
        Assert.Equal(2, completedErrors);
        Assert.Equal(2, failedMessages.Count);
    }

    [Fact]
    public async Task RunAsync_WithResults_OnError_InvokedForFailedMessages()
    {
        // Arrange
        var sp = _fixture.BuildServiceProvider();
        var temporary = sp.GetRequiredService<IRabbitFlowTemporary>();

        var messages = Enumerable.Range(1, 3)
            .Select(i => new TestEvent { Id = i.ToString(), Message = $"item-{i}" })
            .ToList();

        var collectedResults = new List<string>();
        var failedMessages = new List<TestEvent>();

        // Act – message with Id "2" throws
        var processed = await temporary.RunAsync<TestEvent, string>(
            messages,
            async (msg, ct) =>
            {
                if (msg.Id == "2")
                    throw new InvalidOperationException("Simulated failure");

                await Task.CompletedTask;
                return $"processed-{msg.Id}";
            },
            async (count, results) =>
            {
                collectedResults.AddRange(results);
                await Task.CompletedTask;
            },
            onError: async (msg, ct) =>
            {
                lock (failedMessages) { failedMessages.Add(msg); }
                await Task.CompletedTask;
            },
            options: new RunTemporaryOptions { PrefetchCount = 1 });

        // Assert
        Assert.Equal(3, processed);
        Assert.Equal(2, collectedResults.Count);
        Assert.Single(failedMessages);
        Assert.Equal("2", failedMessages[0].Id);
    }

    [Fact]
    public async Task RunAsync_OnError_NullCallback_DoesNotThrow()
    {
        // Arrange
        var sp = _fixture.BuildServiceProvider();
        var temporary = sp.GetRequiredService<IRabbitFlowTemporary>();

        var messages = new List<TestEvent>
        {
            new() { Id = "1", Message = "will-fail" }
        };

        // Act – handler throws but no onError callback provided
        var processed = await temporary.RunAsync<TestEvent>(
            messages,
            async (msg, ct) =>
            {
                throw new Exception("Boom");
            },
            options: new RunTemporaryOptions { PrefetchCount = 1 });

        // Assert – should complete without throwing
        Assert.Equal(1, processed);
    }
}
