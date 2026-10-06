using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using EasyRabbitFlow.Tests.Fixtures;
using EasyRabbitFlow.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace EasyRabbitFlow.Tests;

[Collection("RabbitMq")]
public class TemporaryProgressTests
{
    private readonly RabbitMqFixture _fixture;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    public TemporaryProgressTests(RabbitMqFixture fixture) => _fixture = fixture;

    private static TestEvent Message(string id) => new() { Id = id, Message = id };

    [Fact]
    public async Task Collection_ReportsPeriodicProgress_WithCountersThatOnlyGrow()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        var temporary = sp.GetRequiredService<IRabbitFlowTemporary>();
        var snapshots = new ConcurrentQueue<TemporaryRunProgress>();
        var messages = Enumerable.Range(1, 6).Select(i => Message(i.ToString())).ToList();

        var result = await temporary.RunAsync(messages, async (_, ct) => await Task.Delay(150, ct), options: new RunTemporaryOptions
        {
            CorrelationId = "progress-collection",
            ProgressInterval = TimeSpan.FromMilliseconds(50),
            OnProgress = (progress, _) =>
            {
                snapshots.Enqueue(progress);
                return Task.CompletedTask;
            }
        }).WaitAsync(TestTimeout);

        Assert.True(result.Success);
        var list = snapshots.ToList();
        Assert.True(list.Count >= 2, $"Expected several snapshots, got {list.Count}.");
        Assert.All(list, p =>
        {
            Assert.Equal("progress-collection", p.CorrelationId);
            Assert.Equal(result.QueueName, p.QueueName);
            Assert.Equal(result.StartedUtc, p.StartedUtc);
            Assert.Equal(6, p.TotalMessages);
            Assert.True(p.ProcessedMessages <= p.PublishedMessages);
        });

        for (var i = 1; i < list.Count; i++)
        {
            Assert.True(list[i].ProcessedMessages >= list[i - 1].ProcessedMessages);
            Assert.True(list[i].SucceededMessages >= list[i - 1].SucceededMessages);
            Assert.True(list[i].TimestampUtc >= list[i - 1].TimestampUtc);
        }

        Assert.Contains(list, p => p.ProcessedMessages > 0 && p.ProcessedMessages < 6);
    }

    [Fact]
    public async Task AsyncSource_ReportsProgress_WhileTheSourceIsStillOpen()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        var temporary = sp.GetRequiredService<IRabbitFlowTemporary>();
        var firstReported = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = new ConcurrentQueue<TemporaryRunProgress>();

        async IAsyncEnumerable<TestEvent> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return Message("1");
            // Only a snapshot taken while the source is open releases the second element.
            await firstReported.Task.WaitAsync(ct);
            yield return Message("2");
        }

        var result = await temporary.RunAsync(Source(), (_, _) => Task.CompletedTask, options: new RunTemporaryOptions
        {
            ProgressInterval = TimeSpan.FromMilliseconds(50),
            OnProgress = (progress, _) =>
            {
                snapshots.Enqueue(progress);
                if (progress.ProcessedMessages == 1)
                {
                    firstReported.TrySetResult(true);
                }
                return Task.CompletedTask;
            }
        }).WaitAsync(TestTimeout);

        Assert.True(result.Success);
        Assert.Equal(2, result.TotalMessages);
        Assert.Contains(snapshots, p => p.TotalMessages == 1 && p.ProcessedMessages == 1);
    }

    [Fact]
    public async Task IsAHeartbeat_ReportingEvenWhenNothingChanges()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        var temporary = sp.GetRequiredService<IRabbitFlowTemporary>();
        var snapshots = new ConcurrentQueue<TemporaryRunProgress>();

        await temporary.RunAsync([Message("1")], async (_, ct) => await Task.Delay(400, ct), options: new RunTemporaryOptions
        {
            ProgressInterval = TimeSpan.FromMilliseconds(50),
            OnProgress = (progress, _) =>
            {
                snapshots.Enqueue(progress);
                return Task.CompletedTask;
            }
        }).WaitAsync(TestTimeout);

        var whileBusy = snapshots.Where(p => p.InFlightMessages == 1 && p.ProcessedMessages == 0).ToList();
        Assert.True(whileBusy.Count >= 2, $"Expected repeated snapshots while the handler runs, got {whileBusy.Count}.");
        Assert.True(whileBusy[^1].TimestampUtc > whileBusy[0].TimestampUtc);
    }

    [Fact]
    public async Task Calls_NeverOverlap_WhenTheCallbackIsSlowerThanTheInterval()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        var temporary = sp.GetRequiredService<IRabbitFlowTemporary>();
        var running = 0;
        var maxRunning = 0;
        var calls = 0;

        await temporary.RunAsync([Message("1")], async (_, ct) => await Task.Delay(600, ct), options: new RunTemporaryOptions
        {
            ProgressInterval = TimeSpan.FromMilliseconds(10),
            OnProgress = async (_, ct) =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref maxRunning, now);
                Interlocked.Increment(ref calls);
                try { await Task.Delay(120, ct); }
                finally { Interlocked.Decrement(ref running); }
            }
        }).WaitAsync(TestTimeout);

        Assert.True(calls >= 2, $"Expected several calls, got {calls}.");
        Assert.Equal(1, maxRunning);
    }

    [Fact]
    public async Task CallbackFailure_IsLogged_AndDoesNotAffectTheRun()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        var temporary = sp.GetRequiredService<IRabbitFlowTemporary>();
        var calls = 0;
        var messages = Enumerable.Range(1, 3).Select(i => Message(i.ToString())).ToList();

        var result = await temporary.RunAsync(messages, async (_, ct) => await Task.Delay(100, ct), options: new RunTemporaryOptions
        {
            ProgressInterval = TimeSpan.FromMilliseconds(30),
            OnProgress = (_, _) =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("progress store unavailable");
            }
        }).WaitAsync(TestTimeout);

        Assert.True(calls >= 2, "A failing callback must keep being called on the next intervals.");
        Assert.True(result.Success);
        Assert.Equal(3, result.SucceededMessages);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task NoProgressCall_StartsAfterTheCompletionCallback()
    {
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        var temporary = sp.GetRequiredService<IRabbitFlowTemporary>();
        var completed = 0;
        var callsAfterCompletion = 0;

        await temporary.RunAsync([Message("1"), Message("2")], async (_, ct) => await Task.Delay(100, ct),
            onCompletedAsync: (_, _) =>
            {
                Volatile.Write(ref completed, 1);
                return Task.CompletedTask;
            },
            options: new RunTemporaryOptions
            {
                ProgressInterval = TimeSpan.FromMilliseconds(5),
                OnProgress = (_, _) =>
                {
                    if (Volatile.Read(ref completed) == 1)
                    {
                        Interlocked.Increment(ref callsAfterCompletion);
                    }
                    return Task.CompletedTask;
                }
            }).WaitAsync(TestTimeout);

        // Leave room for a stray timer tick to show up if the loop were still alive.
        await Task.Delay(100);

        Assert.Equal(0, callsAfterCompletion);
    }

    [Fact]
    public void ProgressInterval_MustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunTemporaryOptions { ProgressInterval = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunTemporaryOptions { ProgressInterval = TimeSpan.FromSeconds(-1) });
        Assert.Equal(TimeSpan.FromSeconds(5), new RunTemporaryOptions().ProgressInterval);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
