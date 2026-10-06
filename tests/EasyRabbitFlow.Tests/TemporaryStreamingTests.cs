using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using EasyRabbitFlow.Tests.Fixtures;
using EasyRabbitFlow.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace EasyRabbitFlow.Tests;

[Collection("RabbitMq")]
public class TemporaryStreamingTests
{
    private readonly RabbitMqFixture _fixture;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    public TemporaryStreamingTests(RabbitMqFixture fixture) => _fixture = fixture;

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TestEvent Message(string id) => new() { Id = id, Message = id };

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(25, ct);
        }
    }

    [Fact]
    public async Task ProcessesBeforeSourceEnds_AndDrainsAfterSourceCompletion()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        using var deadline = new CancellationTokenSource(TestTimeout);
        var temporary = sp.GetRequiredService<IRabbitFlowTemporary>();
        var firstProcessed = Signal();
        var secondStarted = Signal();
        var releaseSecond = Signal();
        var sourceEnded = Signal();
        var enumerations = 0;
        var completions = 0;

        async IAsyncEnumerable<TestEvent> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            Interlocked.Increment(ref enumerations);
            try
            {
                yield return Message("1");
                // Materializing the source before consuming would deadlock here.
                await firstProcessed.Task.WaitAsync(ct);
                yield return Message("2");
            }
            finally { sourceEnded.TrySetResult(true); }
        }

        var run = temporary.RunAsync(Source(), async (message, ct) =>
        {
            if (message.Id == "1") firstProcessed.TrySetResult(true);
            else
            {
                secondStarted.TrySetResult(true);
                await releaseSecond.Task.WaitAsync(ct);
            }
        }, onCompleted: _ => Interlocked.Increment(ref completions), cancellationToken: deadline.Token);

        await sourceEnded.Task.WaitAsync(deadline.Token);
        await secondStarted.Task.WaitAsync(deadline.Token);
        Assert.False(run.IsCompleted);
        releaseSecond.TrySetResult(true);
        var result = await run.WaitAsync(deadline.Token);

        Assert.True(result.Success);
        Assert.True(result.SourceCompleted);
        Assert.Equal(2, result.TotalMessages);
        Assert.Equal(2, result.PublishedMessages);
        Assert.Equal(2, result.SucceededMessages);
        Assert.Equal(1, enumerations);
        Assert.Equal(1, completions);
        Assert.False((await sp.GetRequiredService<IRabbitFlowState>()
            .GetQueueStateAsync(result.QueueName!, deadline.Token)).Exists);
    }

    [Fact]
    public async Task ChannelSource_RemainsOpenUntilWriterCompletes()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        using var deadline = new CancellationTokenSource(TestTimeout);
        var source = Channel.CreateUnbounded<TestEvent>();
        var processed = Signal();
        var run = sp.GetRequiredService<IRabbitFlowTemporary>().RunAsync(source.Reader.ReadAllAsync(),
            (_, _) => { processed.TrySetResult(true); return Task.CompletedTask; },
            cancellationToken: deadline.Token);

        await source.Writer.WriteAsync(Message("first"), deadline.Token);
        await processed.Task.WaitAsync(deadline.Token);
        Assert.False(run.IsCompleted);
        await source.Writer.WriteAsync(Message("later"), deadline.Token);
        source.Writer.Complete();
        var result = await run.WaitAsync(deadline.Token);
        Assert.True(result.Success);
        Assert.True(result.SourceCompleted);
        Assert.Equal(2, result.ProcessedMessages);
    }

    [Fact]
    public async Task EmptySource_InvokesAsyncCompletionOnce_AndDisposes()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        var disposed = false;
        var callbacks = 0;
        async IAsyncEnumerable<TestEvent> Source()
        {
            try { await Task.CompletedTask; yield break; }
            finally { disposed = true; }
        }

        var result = await sp.GetRequiredService<IRabbitFlowTemporary>().RunAsync(Source(),
            (_, _) => throw new InvalidOperationException("No handler expected"),
            onCompletedAsync: (snapshot, _) =>
            {
                Assert.True(disposed);
                Assert.True(snapshot.SourceCompleted);
                Interlocked.Increment(ref callbacks);
                return Task.CompletedTask;
            }).WaitAsync(TestTimeout);

        Assert.True(result.Success);
        Assert.Equal(0, result.TotalMessages);
        Assert.Empty(result.Errors);
        Assert.Equal(1, callbacks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceFailure_ReportsPartialResult_WithoutInventingFailedMessage(bool yieldMessage)
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        var disposed = false;
        var messageErrors = 0;
        async IAsyncEnumerable<TestEvent> Source()
        {
            try
            {
                if (yieldMessage) yield return Message("1");
                await Task.CompletedTask;
                throw new InvalidOperationException("Source unavailable");
            }
            finally { disposed = true; }
        }

        var result = await sp.GetRequiredService<IRabbitFlowTemporary>().RunAsync(Source(),
            (_, _) => Task.CompletedTask,
            onError: (_, _) => { messageErrors++; return Task.CompletedTask; }).WaitAsync(TestTimeout);

        Assert.False(result.Success);
        Assert.False(result.SourceCompleted);
        Assert.Equal(yieldMessage ? 1 : 0, result.TotalMessages);
        Assert.Equal(result.TotalMessages, result.SucceededMessages);
        Assert.Equal(0, result.FailedMessages);
        Assert.Equal(0, messageErrors);
        Assert.True(disposed);
        var error = Assert.Single(result.Errors);
        Assert.Equal(TemporaryRunErrorStage.Enumeration, error.Stage);
        Assert.Equal("Source unavailable", error.Message);
        Assert.Null(error.MessageIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationWhileWaitingForSource_ReturnsPartialResult_AndDisposes(bool yieldMessage)
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var waiting = Signal();
        var disposed = Signal();
        var processed = Signal();
        async IAsyncEnumerable<TestEvent> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            try
            {
                if (yieldMessage)
                {
                    yield return Message("1");
                    await processed.Task.WaitAsync(ct);
                }
                waiting.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally { disposed.TrySetResult(true); }
        }

        var run = sp.GetRequiredService<IRabbitFlowTemporary>().RunAsync(Source(),
            (_, _) => { processed.TrySetResult(true); return Task.CompletedTask; },
            cancellationToken: cancellation.Token);
        await waiting.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();
        var result = await run.WaitAsync(TestTimeout);
        await disposed.Task.WaitAsync(TestTimeout);
        Assert.False(result.Success);
        Assert.False(result.SourceCompleted);
        Assert.Equal(yieldMessage ? 1 : 0, result.TotalMessages);
        Assert.Equal(result.TotalMessages, result.SucceededMessages);
        Assert.Contains(result.Errors, e => e.Stage == TemporaryRunErrorStage.Cancellation);
    }

    [Fact]
    public async Task RunTimeout_CancelsWaitingSource_EvenBeforeFirstMessage()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        var disposed = Signal();
        async IAsyncEnumerable<TestEvent> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
                yield return Message("unreachable");
            }
            finally { disposed.TrySetResult(true); }
        }

        var result = await sp.GetRequiredService<IRabbitFlowTemporary>().RunAsync(Source(),
            (_, _) => Task.CompletedTask,
            options: new RunTemporaryOptions { RunTimeout = TimeSpan.FromSeconds(2) }).WaitAsync(TestTimeout);
        await disposed.Task.WaitAsync(TestTimeout);
        Assert.False(result.Success);
        Assert.False(result.SourceCompleted);
        Assert.Equal(0, result.TotalMessages);
        Assert.Contains(result.Errors, e => e.Stage == TemporaryRunErrorStage.Timeout);
    }

    [Fact]
    public async Task NonCooperativeSource_DoesNotBlockCancellation_AndIsDisposedAfterMoveSettles()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        var disposed = Signal();
        async IAsyncEnumerable<TestEvent> Source()
        {
            try
            {
                entered.TrySetResult(true);
                // Intentionally ignores the supplied token.
                if (await release.Task) throw new InvalidOperationException("Late failure must be observed");
                yield break;
            }
            finally { disposed.TrySetResult(true); }
        }

        try
        {
            var run = sp.GetRequiredService<IRabbitFlowTemporary>().RunAsync(Source(),
                (_, _) => Task.CompletedTask, cancellationToken: cancellation.Token);
            await entered.Task.WaitAsync(TestTimeout);
            cancellation.Cancel();
            var result = await run.WaitAsync(TestTimeout);
            Assert.False(result.Success);
            Assert.False(result.SourceCompleted);
            Assert.False(disposed.Task.IsCompleted);
        }
        finally { release.TrySetResult(true); }
        await disposed.Task.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task ResultsOverload_CollectsSuccesses_AndReportsHandlerErrors()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        var errors = new List<string>();
        TemporaryRunResult<string>? callback = null;
        async IAsyncEnumerable<TestEvent> Source()
        {
            yield return Message("ok");
            await Task.Yield();
            yield return Message("bad");
        }

        var result = await sp.GetRequiredService<IRabbitFlowTemporary>().RunAsync<TestEvent, string>(
            Source(), (message, _) => message.Id == "bad"
                ? Task.FromException<string>(new InvalidOperationException("Handler failed"))
                : Task.FromResult(message.Id),
            (snapshot, _) => { callback = snapshot; return Task.CompletedTask; },
            onError: (message, _) => { errors.Add(message.Id); return Task.CompletedTask; }).WaitAsync(TestTimeout);

        Assert.Same(result, callback);
        Assert.True(result.SourceCompleted);
        Assert.False(result.Success);
        Assert.Equal(2, result.TotalMessages);
        Assert.Equal(1, result.FailedMessages);
        Assert.Equal("ok", Assert.Single(result.Results));
        Assert.Equal("bad", Assert.Single(errors));
    }

    [Fact]
    public async Task PublishFailure_ReportsSourceIndex_AndContinuesEnumeration()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        var errors = new List<string>();
        async IAsyncEnumerable<PoisonSerializationEvent> Source()
        {
            yield return new() { Id = "0" };
            await Task.Yield();
            yield return new() { Id = "1", ShouldThrow = true };
            yield return new() { Id = "2" };
        }

        var result = await sp.GetRequiredService<IRabbitFlowTemporary>().RunAsync(Source(),
            (_, _) => Task.CompletedTask,
            onError: (message, _) => { errors.Add(message.Id); return Task.CompletedTask; }).WaitAsync(TestTimeout);
        Assert.True(result.SourceCompleted);
        Assert.False(result.Success);
        Assert.Equal(3, result.TotalMessages);
        Assert.Equal(2, result.SucceededMessages);
        Assert.Equal("1", Assert.Single(errors));
        var error = Assert.Single(result.Errors);
        Assert.Equal(TemporaryRunErrorStage.Publish, error.Stage);
        Assert.Equal(1, error.MessageIndex);
    }

    [Fact]
    public async Task NullSource_IsRejected()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        await Assert.ThrowsAsync<ArgumentNullException>(() => sp.GetRequiredService<IRabbitFlowTemporary>()
            .RunAsync((IAsyncEnumerable<TestEvent>)null!, (_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task DisposalFailure_IsReportedAsEnumerationFailure()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        async IAsyncEnumerable<TestEvent> Source()
        {
            try { yield return Message("1"); await Task.CompletedTask; }
            finally { throw new InvalidOperationException("Disposal failed"); }
        }
        var result = await sp.GetRequiredService<IRabbitFlowTemporary>().RunAsync(Source(),
            (_, _) => Task.CompletedTask).WaitAsync(TestTimeout);
        Assert.False(result.Success);
        Assert.False(result.SourceCompleted);
        Assert.Equal(1, result.SucceededMessages);
        var error = Assert.Single(result.Errors);
        Assert.Equal(TemporaryRunErrorStage.Enumeration, error.Stage);
        Assert.Equal("Disposal failed", error.Message);
    }

    [Fact]
    public async Task CancellationWhileDisposing_DoesNotWaitForever()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var disposing = Signal();
        var release = Signal();
        var disposed = Signal();
        async IAsyncEnumerable<TestEvent> Source()
        {
            try { yield break; }
            finally
            {
                disposing.TrySetResult(true);
                await release.Task;
                disposed.TrySetResult(true);
            }
        }
        try
        {
            var run = sp.GetRequiredService<IRabbitFlowTemporary>().RunAsync(Source(),
                (_, _) => Task.CompletedTask, cancellationToken: cancellation.Token);
            await disposing.Task.WaitAsync(TestTimeout);
            cancellation.Cancel();
            var result = await run.WaitAsync(TestTimeout);
            Assert.False(result.Success);
            Assert.False(result.SourceCompleted);
            Assert.False(disposed.Task.IsCompleted);
        }
        finally { release.TrySetResult(true); }
        await disposed.Task.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task CancellationAfterSourceEnds_CancelsOutstandingHandler()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var started = Signal();
        var sourceEnded = Signal();
        async IAsyncEnumerable<TestEvent> Source()
        {
            yield return Message("1");
            await Task.CompletedTask;
            sourceEnded.TrySetResult(true);
        }
        var run = sp.GetRequiredService<IRabbitFlowTemporary>().RunAsync(Source(), async (_, ct) =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
        }, cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TestTimeout);
        await sourceEnded.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();
        var result = await run.WaitAsync(TestTimeout);
        Assert.True(result.SourceCompleted);
        Assert.False(result.Success);
        Assert.Equal(1, result.FailedMessages);
        Assert.Contains(result.Errors, e => e.Stage == TemporaryRunErrorStage.Cancellation);
    }

    [Fact]
    public async Task SourceFailure_DrainsPreviouslyAdmittedHandler()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        using var deadline = new CancellationTokenSource(TestTimeout);
        var sourceFailed = Signal();
        var started = Signal();
        var release = Signal();
        async IAsyncEnumerable<TestEvent> Source()
        {
            yield return Message("1");
            await started.Task.WaitAsync(deadline.Token);
            sourceFailed.TrySetResult(true);
            throw new InvalidOperationException("End failed");
        }
        var run = sp.GetRequiredService<IRabbitFlowTemporary>().RunAsync(Source(), async (_, ct) =>
        {
            started.TrySetResult(true);
            await release.Task.WaitAsync(ct);
        }, cancellationToken: deadline.Token);
        await sourceFailed.Task.WaitAsync(deadline.Token);
        Assert.False(run.IsCompleted);
        release.TrySetResult(true);
        var result = await run.WaitAsync(deadline.Token);
        Assert.False(result.SourceCompleted);
        Assert.Equal(1, result.SucceededMessages);
        Assert.Equal(0, result.FailedMessages);
    }

    [Fact]
    public async Task BrokerDisconnect_InterruptsWaitingSource()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var waiting = Signal();
        var disposed = Signal();
        async IAsyncEnumerable<TestEvent> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            try
            {
                waiting.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, ct);
                yield break;
            }
            finally { disposed.TrySetResult(true); }
        }
        var run = sp.GetRequiredService<IRabbitFlowTemporary>().RunAsync(Source(),
            (_, _) => Task.CompletedTask, cancellationToken: deadline.Token);
        await waiting.Task.WaitAsync(deadline.Token);
        Assert.Equal(0, await _fixture.CloseAllConnectionsAsync("streaming-test"));
        var result = await run.WaitAsync(deadline.Token);
        await disposed.Task.WaitAsync(deadline.Token);
        Assert.False(result.SourceCompleted);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Stage == TemporaryRunErrorStage.ConnectionLost);
    }

    [Fact]
    public async Task BrokerDisconnect_InterruptsWaitingSource_ButLetsInFlightHandlerFinish()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var handlerStarted = Signal();
        var release = Signal();
        var sourceDisposed = Signal();
        var handlerOutcome = "not-run";

        async IAsyncEnumerable<TestEvent> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            try
            {
                yield return Message("1");
                // Waits for a "next page" that never arrives: only the broker outage can end this wait.
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally { sourceDisposed.TrySetResult(true); }
        }

        var run = sp.GetRequiredService<IRabbitFlowTemporary>().RunAsync(Source(), async (_, ct) =>
        {
            handlerStarted.TrySetResult(true);
            try { await release.Task.WaitAsync(ct); handlerOutcome = "completed"; }
            catch (OperationCanceledException) { handlerOutcome = "canceled"; throw; }
        }, cancellationToken: deadline.Token);

        await handlerStarted.Task.WaitAsync(deadline.Token);
        Assert.Equal(0, await _fixture.CloseAllConnectionsAsync("streaming-test"));
        // The source wait is interrupted by the outage; the handler, which already owns its message, is not.
        await sourceDisposed.Task.WaitAsync(deadline.Token);
        await Task.Delay(500);
        Assert.False(run.IsCompleted);
        Assert.Equal("not-run", handlerOutcome);

        release.TrySetResult(true);
        var result = await run.WaitAsync(deadline.Token);

        Assert.Equal("completed", handlerOutcome);
        Assert.False(result.Success);
        Assert.False(result.SourceCompleted);
        Assert.Equal(1, result.TotalMessages);
        Assert.Equal(1, result.SucceededMessages);
        Assert.Equal(0, result.FailedMessages);
        Assert.Equal(TemporaryRunErrorStage.ConnectionLost, Assert.Single(result.Errors).Stage);
    }

    [Fact]
    public async Task FastSource_WithParallelHandlers_AccountsForEveryMessage()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        var received = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        async IAsyncEnumerable<TestEvent> Source()
        {
            for (var i = 0; i < 500; i++) yield return Message(i.ToString());
            await Task.CompletedTask;
        }
        var result = await sp.GetRequiredService<IRabbitFlowTemporary>().RunAsync(Source(), async (message, _) =>
        {
            await Task.Yield();
            received.AddOrUpdate(message.Id, 1, (_, count) => count + 1);
        }, options: new RunTemporaryOptions { PrefetchCount = 8, RunTimeout = TestTimeout }).WaitAsync(TestTimeout);
        Assert.True(result.Success);
        Assert.True(result.SourceCompleted);
        Assert.Equal(500, result.TotalMessages);
        Assert.Equal(500, result.ProcessedMessages);
        Assert.Equal(500, received.Count);
        Assert.All(received.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task CompletionFailure_PreservesSourceState_AndReportsError()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        async IAsyncEnumerable<TestEvent> Source()
        {
            yield return Message("1");
            await Task.CompletedTask;
        }
        var result = await sp.GetRequiredService<IRabbitFlowTemporary>().RunAsync(Source(),
            (_, _) => Task.CompletedTask,
            onCompletedAsync: (_, _) => throw new InvalidOperationException("Completion failed")).WaitAsync(TestTimeout);
        Assert.True(result.SourceCompleted);
        Assert.Equal(1, result.SucceededMessages);
        Assert.Equal(TemporaryRunErrorStage.Completion, Assert.Single(result.Errors).Stage);
    }

    [Fact]
    public async Task MaxInFlightMessages_PausesSource_UntilHandlersReachTerminalState()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        using var deadline = new CancellationTokenSource(TestTimeout);
        var pulled = 0;
        var handlersStarted = 0;
        var threeStarted = Signal();
        var releaseFirst = Signal();
        var releaseRest = Signal();

        async IAsyncEnumerable<TestEvent> Source()
        {
            for (var i = 1; i <= 10; i++)
            {
                Interlocked.Increment(ref pulled);
                yield return Message(i.ToString());
            }
            await Task.CompletedTask;
        }

        var run = sp.GetRequiredService<IRabbitFlowTemporary>().RunAsync(Source(), async (message, ct) =>
        {
            if (Interlocked.Increment(ref handlersStarted) == 3) threeStarted.TrySetResult(true);
            await (message.Id == "1" ? releaseFirst.Task : releaseRest.Task).WaitAsync(ct);
        }, options: new RunTemporaryOptions { PrefetchCount = 3, MaxInFlightMessages = 3 }, cancellationToken: deadline.Token);

        // Three elements in flight: the fourth is never requested from the source while none has finished.
        await threeStarted.Task.WaitAsync(deadline.Token);
        await Task.Delay(500);
        Assert.Equal(3, Volatile.Read(ref pulled));
        Assert.False(run.IsCompleted);

        // One terminal state frees exactly one permit: exactly one more element is pulled.
        releaseFirst.TrySetResult(true);
        await WaitUntilAsync(() => Volatile.Read(ref pulled) == 4, deadline.Token);
        await Task.Delay(300);
        Assert.Equal(4, Volatile.Read(ref pulled));

        releaseRest.TrySetResult(true);
        var result = await run.WaitAsync(deadline.Token);
        Assert.True(result.Success);
        Assert.True(result.SourceCompleted);
        Assert.Equal(10, result.TotalMessages);
        Assert.Equal(10, result.SucceededMessages);
    }

    [Fact]
    public async Task MaxInFlightMessages_CancellationWhileWaitingForPermit_ReturnsPartialResult_AndDisposesSource()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var started = Signal();
        var disposed = Signal();
        var pulled = 0;

        async IAsyncEnumerable<TestEvent> Source()
        {
            try
            {
                Interlocked.Increment(ref pulled);
                yield return Message("1");
                Interlocked.Increment(ref pulled);
                yield return Message("never-requested");
                await Task.CompletedTask;
            }
            finally { disposed.TrySetResult(true); }
        }

        var run = sp.GetRequiredService<IRabbitFlowTemporary>().RunAsync(Source(), async (_, ct) =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
        }, options: new RunTemporaryOptions { MaxInFlightMessages = 1 }, cancellationToken: cancellation.Token);

        await started.Task.WaitAsync(TestTimeout);
        await Task.Delay(300);
        Assert.Equal(1, Volatile.Read(ref pulled));

        cancellation.Cancel();
        var result = await run.WaitAsync(TestTimeout);
        await disposed.Task.WaitAsync(TestTimeout);

        Assert.False(result.SourceCompleted);
        Assert.Equal(1, result.TotalMessages);
        Assert.Equal(1, result.FailedMessages);
        Assert.Contains(result.Errors, e => e.Stage == TemporaryRunErrorStage.Cancellation);
    }

    [Fact]
    public async Task MaxInFlightMessages_Invalid_IsRejected_BeforeTouchingSourceOrBroker()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        var pulled = false;
        async IAsyncEnumerable<TestEvent> Source()
        {
            pulled = true;
            yield return Message("1");
            await Task.CompletedTask;
        }

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => sp.GetRequiredService<IRabbitFlowTemporary>().RunAsync(Source(),
            (_, _) => Task.CompletedTask,
            options: new RunTemporaryOptions { PrefetchCount = 4, MaxInFlightMessages = 2 }));
        Assert.Contains("MaxInFlightMessages", ex.Message);
        Assert.False(pulled);

        Assert.Throws<ArgumentOutOfRangeException>(() => new RunTemporaryOptions { MaxInFlightMessages = 0 });
    }

    [Fact]
    public async Task MaxInFlightMessages_IsIgnored_ByCollectionOverloads()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        var messages = Enumerable.Range(1, 5).Select(i => Message(i.ToString())).ToList();

        // Would be rejected for an asynchronous source (below PrefetchCount); collections never gate.
        var result = await sp.GetRequiredService<IRabbitFlowTemporary>().RunAsync(messages,
            (_, _) => Task.CompletedTask,
            options: new RunTemporaryOptions { PrefetchCount = 4, MaxInFlightMessages = 1 }).WaitAsync(TestTimeout);

        Assert.True(result.Success);
        Assert.Equal(5, result.SucceededMessages);
    }
}
