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
        var result = await temporary.RunAsync<TestEvent>(
            messages,
            async (msg, ct) =>
            {
                lock (received) { received.Add(msg); }
                await Task.CompletedTask;
            });

        // Assert
        Assert.True(result.Success);
        Assert.Equal(5, result.TotalMessages);
        Assert.Equal(5, result.PublishedMessages);
        Assert.Equal(5, result.ProcessedMessages);
        Assert.Equal(5, result.SucceededMessages);
        Assert.Equal(0, result.FailedMessages);
        Assert.Equal(5, received.Count);
    }

    [Fact]
    public async Task RunAsync_EmptyList_ReturnsZero()
    {
        var sp = _fixture.BuildServiceProvider();
        var temporary = sp.GetRequiredService<IRabbitFlowTemporary>();

        var result = await temporary.RunAsync<TestEvent>(
            new List<TestEvent>(),
            async (msg, ct) => await Task.CompletedTask);

        Assert.True(result.Success);
        Assert.Equal(0, result.TotalMessages);
        Assert.Equal(0, result.ProcessedMessages);
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
        TemporaryRunResult? callbackResult = null;

        // Act
        await temporary.RunAsync<TestEvent>(
            messages,
            async (msg, ct) => await Task.CompletedTask,
            onCompleted: r => { completedCount = r.ProcessedMessages; errorCount = r.FailedMessages; callbackResult = r; });

        // Assert
        Assert.Equal(2, completedCount);
        Assert.Equal(0, errorCount);
        // The callback now receives the full run result, not just counters.
        Assert.NotNull(callbackResult);
        Assert.True(callbackResult!.Success);
        Assert.Equal(2, callbackResult.TotalMessages);
        Assert.Equal(2, callbackResult.SucceededMessages);
        Assert.Empty(callbackResult.Errors);
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
        var result = await temporary.RunAsync<TestEvent, string>(
            messages,
            async (msg, ct) =>
            {
                await Task.CompletedTask;
                return $"processed-{msg.Id}";
            },
            async (runResult, ct) =>
            {
                collectedResults.AddRange(runResult.Results);
                await Task.CompletedTask;
            });

        // Assert
        Assert.True(result.Success);
        Assert.Equal(3, result.ProcessedMessages);
        Assert.Equal(3, result.SucceededMessages);
        Assert.Equal(0, result.FailedMessages);
        Assert.Equal(3, result.Results.Count);
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
        var result = await temporary.RunAsync<TestEvent>(
            messages,
            async (msg, ct) =>
            {
                lock (received) { received.Add(msg); }
                await Task.CompletedTask;
            },
            options: options);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(1, result.ProcessedMessages);
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
        var result = await temporary.RunAsync<TestEvent>(
            messages,
            async (msg, ct) =>
            {
                if (msg.Id == "2")
                    throw new InvalidOperationException("Simulated failure");

                await Task.CompletedTask;
            },
            onCompleted: _ => { },
            onError: async (msg, ct) =>
            {
                lock (failedMessages) { failedMessages.Add(msg); }
                await Task.CompletedTask;
            },
            options: new RunTemporaryOptions { PrefetchCount = 1 });

        // Assert
        Assert.False(result.Success);
        Assert.Equal(3, result.ProcessedMessages);
        Assert.Equal(2, result.SucceededMessages);
        Assert.Equal(1, result.FailedMessages);
        Assert.Single(result.Errors);
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
        var result = await temporary.RunAsync<TestEvent>(
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
        Assert.False(result.Success);
        Assert.Equal(1, result.ProcessedMessages);
        Assert.Equal(0, result.SucceededMessages);
        Assert.Equal(1, result.FailedMessages);
        Assert.Single(result.Errors);
        Assert.Equal(TemporaryRunErrorStage.Timeout, result.Errors[0].Stage);
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
        var result = await temporary.RunAsync<TestEvent>(
            messages,
            async (msg, ct) =>
            {
                if (int.Parse(msg.Id) % 2 == 0)
                    throw new Exception("Even fail");

                await Task.CompletedTask;
            },
            onCompleted: r => { completedTotal = r.ProcessedMessages; completedErrors = r.FailedMessages; },
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
        Assert.False(result.Success);
        Assert.Equal(4, result.ProcessedMessages);
        Assert.Equal(2, result.SucceededMessages);
        Assert.Equal(2, result.FailedMessages);
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
        var result = await temporary.RunAsync<TestEvent, string>(
            messages,
            async (msg, ct) =>
            {
                if (msg.Id == "2")
                    throw new InvalidOperationException("Simulated failure");

                await Task.CompletedTask;
                return $"processed-{msg.Id}";
            },
            async (runResult, ct) =>
            {
                collectedResults.AddRange(runResult.Results);
                await Task.CompletedTask;
            },
            onError: async (msg, ct) =>
            {
                lock (failedMessages) { failedMessages.Add(msg); }
                await Task.CompletedTask;
            },
            options: new RunTemporaryOptions { PrefetchCount = 1 });

        // Assert
        Assert.False(result.Success);
        Assert.Equal(3, result.ProcessedMessages);
        Assert.Equal(2, result.SucceededMessages);
        Assert.Equal(1, result.FailedMessages);
        Assert.Equal(2, result.Results.Count);
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
        var result = await temporary.RunAsync<TestEvent>(
            messages,
            async (msg, ct) =>
            {
                throw new Exception("Boom");
            },
            options: new RunTemporaryOptions { PrefetchCount = 1 });

        // Assert – should complete without throwing
        Assert.False(result.Success);
        Assert.Equal(1, result.ProcessedMessages);
        Assert.Equal(1, result.FailedMessages);
    }

    [Fact]
    public async Task RunAsync_PublishFailure_ReportsMessageIndex_And_InvokesOnError()
    {
        // Arrange
        var sp = _fixture.BuildServiceProvider();
        var temporary = sp.GetRequiredService<IRabbitFlowTemporary>();

        var messages = new List<PoisonSerializationEvent>
        {
            new() { Id = "0" },
            new() { Id = "1", ShouldThrow = true },
            new() { Id = "2" }
        };

        var received = new List<string>();
        var failedMessages = new List<PoisonSerializationEvent>();

        // Act – message at index 1 fails to serialize during publish
        var result = await temporary.RunAsync(
            messages,
            async (msg, ct) =>
            {
                lock (received) { received.Add(msg.Id); }
                await Task.CompletedTask;
            },
            onError: (msg, ct) =>
            {
                lock (failedMessages) { failedMessages.Add(msg); }
                return Task.CompletedTask;
            },
            options: new RunTemporaryOptions { PrefetchCount = 1 });

        // Assert
        Assert.False(result.Success);
        Assert.Equal(3, result.TotalMessages);
        Assert.Equal(2, result.PublishedMessages);
        Assert.Equal(2, result.ProcessedMessages);
        Assert.Equal(2, result.SucceededMessages);
        Assert.Equal(1, result.FailedMessages);
        Assert.Equal(2, received.Count);

        var error = Assert.Single(result.Errors);
        Assert.Equal(TemporaryRunErrorStage.Publish, error.Stage);
        Assert.Equal(1, error.MessageIndex);

        var failed = Assert.Single(failedMessages);
        Assert.Equal("1", failed.Id);
    }

    [Fact]
    public async Task RunAsync_RunTimeout_CompletesEarly_And_ReportsUnprocessedAsFailed()
    {
        // Arrange
        var sp = _fixture.BuildServiceProvider();
        var temporary = sp.GetRequiredService<IRabbitFlowTemporary>();

        var messages = Enumerable.Range(1, 3)
            .Select(i => new TestEvent { Id = i.ToString(), Message = $"msg-{i}" })
            .ToList();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Act – every handler outlives the run timeout; without RunTimeout this would take 30s+
        var result = await temporary.RunAsync<TestEvent>(
            messages,
            async (msg, ct) => await Task.Delay(TimeSpan.FromSeconds(30), ct),
            options: new RunTemporaryOptions { PrefetchCount = 1, RunTimeout = TimeSpan.FromSeconds(2) });

        stopwatch.Stop();

        // Assert — how many messages reach a terminal state before the cutoff depends on
        // delivery/cancellation interleaving; the invariant is that none succeed and all 3 end up failed.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"Run took {stopwatch.Elapsed}, expected to end shortly after the 2s RunTimeout.");
        Assert.False(result.Success);
        Assert.Equal(3, result.TotalMessages);
        Assert.Equal(3, result.PublishedMessages);
        Assert.InRange(result.ProcessedMessages, 0, 3);
        Assert.Equal(0, result.SucceededMessages);
        Assert.Equal(3, result.FailedMessages);
        Assert.Contains(result.Errors, e => e.Stage == TemporaryRunErrorStage.Timeout);
    }
}
