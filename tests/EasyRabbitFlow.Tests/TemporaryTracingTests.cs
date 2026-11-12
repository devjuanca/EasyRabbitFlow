using System.Collections.Concurrent;
using System.Diagnostics;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using EasyRabbitFlow.Tests.Fixtures;
using EasyRabbitFlow.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace EasyRabbitFlow.Tests;

[Collection("RabbitMq")]
public class TemporaryTracingTests
{
    private readonly RabbitMqFixture _fixture;

    public TemporaryTracingTests(RabbitMqFixture fixture)
    {
        _fixture = fixture;
    }

    // Collects the spans of one run only: the prefix is unique per test, and it names the span.
    private static ActivityListener Listen(string prefix, ConcurrentBag<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RabbitFlowDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.DisplayName == $"{prefix}-temp-queue process")
                {
                    stopped.Add(activity);
                }
            }
        };

        ActivitySource.AddActivityListener(listener);

        return listener;
    }

    private static Activity StartRoot()
    {
        var root = new Activity("test-root");
        root.SetIdFormat(ActivityIdFormat.W3C);
        root.Start();
        return root;
    }

    private static List<TestEvent> Messages(int count) => Enumerable.Range(1, count)
        .Select(i => new TestEvent { Id = i.ToString(), Message = $"msg-{i}" })
        .ToList();

    [Fact]
    public async Task Child_StartsOneSpanPerMessage_ParentedToTheCallerContext()
    {
        var prefix = $"trace-child-{Guid.NewGuid():N}";
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(prefix, stopped);

        var temporary = _fixture.BuildServiceProvider().GetRequiredService<IRabbitFlowTemporary>();
        var seenInHandler = new ConcurrentBag<ActivitySpanId>();

        using var root = StartRoot();

        var result = await temporary.RunAsync<TestEvent>(
            Messages(5),
            async (_, ct) =>
            {
                seenInHandler.Add(Activity.Current!.SpanId);
                await Task.Delay(50, ct);
            },
            options: new RunTemporaryOptions { QueuePrefixName = prefix, PrefetchCount = 3, CorrelationId = "run-child" });

        root.Stop();

        Assert.True(result.Success);
        Assert.Equal(5, stopped.Count);

        foreach (var span in stopped)
        {
            Assert.Equal(ActivityKind.Consumer, span.Kind);
            Assert.Equal(root.TraceId, span.TraceId);
            Assert.Equal(root.SpanId, span.ParentSpanId);
            Assert.Empty(span.Links);
            Assert.Equal(ActivityStatusCode.Unset, span.Status);
            Assert.Equal("rabbitmq", span.GetTagItem("messaging.system"));
            Assert.Equal("process", span.GetTagItem("messaging.operation.type"));
            Assert.Equal($"{prefix}-temp-queue", span.GetTagItem("messaging.destination.name"));
            Assert.Equal(true, span.GetTagItem("messaging.destination.temporary"));
            Assert.Equal("run-child", span.GetTagItem("messaging.message.conversation_id"));
            Assert.Equal(result.QueueName, span.GetTagItem("easyrabbitflow.temporary.queue"));
        }

        // Each handler ran inside its own message span.
        Assert.Equal(
            stopped.Select(s => s.SpanId).OrderBy(id => id.ToString()),
            seenInHandler.OrderBy(id => id.ToString()));
    }

    [Fact]
    public async Task LinkedRoot_StartsANewTracePerMessage_LinkedToTheCallerContext()
    {
        var prefix = $"trace-linked-{Guid.NewGuid():N}";
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(prefix, stopped);

        // Stands in for an instrumented HTTP or database call made by the handler.
        using var downstreamSource = new ActivitySource($"downstream-{prefix}");
        var downstream = new ConcurrentBag<Activity>();
        using var downstreamListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == downstreamSource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = downstream.Add
        };
        ActivitySource.AddActivityListener(downstreamListener);

        var temporary = _fixture.BuildServiceProvider().GetRequiredService<IRabbitFlowTemporary>();
        var traceByMessage = new ConcurrentDictionary<string, ActivityTraceId>();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Fire-and-forget fan-out: the caller's span ends while the handlers are still running.
        var root = StartRoot();
        var rootContext = root.Context;

        var run = temporary.RunAsync<TestEvent>(
            Messages(6),
            async (msg, ct) =>
            {
                await release.Task.WaitAsync(ct);
                await Task.Yield();
                // Still the message's own trace after asynchronous work, with other messages running beside it.
                traceByMessage[msg.Id] = Activity.Current!.TraceId;
                using var call = downstreamSource.StartActivity("downstream call", ActivityKind.Client);
                await Task.Delay(20, ct);
            },
            options: new RunTemporaryOptions { QueuePrefixName = prefix, PrefetchCount = 6, TracingMode = TemporaryTracingMode.LinkedRoot });

        root.Stop();
        Activity.Current = null;
        release.TrySetResult(true);

        var result = await run;

        Assert.True(result.Success);
        Assert.Equal(6, stopped.Count);
        Assert.Equal(6, stopped.Select(s => s.TraceId).Distinct().Count());

        foreach (var span in stopped)
        {
            Assert.NotEqual(rootContext.TraceId, span.TraceId);
            Assert.Equal(default, span.ParentSpanId);
            var link = Assert.Single(span.Links);
            Assert.Equal(rootContext.TraceId, link.Context.TraceId);
            Assert.Equal(rootContext.SpanId, link.Context.SpanId);
        }

        // No two handlers shared a trace, and each one saw the trace of a message span.
        Assert.Equal(6, traceByMessage.Values.Distinct().Count());
        Assert.Equal(
            stopped.Select(s => s.TraceId.ToString()).OrderBy(id => id),
            traceByMessage.Values.Select(id => id.ToString()).OrderBy(id => id));

        // Each downstream span hangs from a different message span, in that message's trace, not the caller's.
        Assert.Equal(6, downstream.Count);
        Assert.Equal(
            stopped.Select(s => s.SpanId.ToString()).OrderBy(id => id),
            downstream.Select(d => d.ParentSpanId.ToString()).OrderBy(id => id));
        Assert.All(downstream, d =>
        {
            Assert.NotEqual(rootContext.TraceId, d.TraceId);
            Assert.Equal(stopped.Single(s => s.SpanId == d.ParentSpanId).TraceId, d.TraceId);
        });
    }

    [Fact]
    public async Task Child_DownstreamSpans_AreChildrenOfTheMessageSpan()
    {
        var prefix = $"trace-child-down-{Guid.NewGuid():N}";
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(prefix, stopped);

        using var downstreamSource = new ActivitySource($"downstream-{prefix}");
        var downstream = new ConcurrentBag<Activity>();
        using var downstreamListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == downstreamSource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = downstream.Add
        };
        ActivitySource.AddActivityListener(downstreamListener);

        var temporary = _fixture.BuildServiceProvider().GetRequiredService<IRabbitFlowTemporary>();

        using var root = StartRoot();

        var result = await temporary.RunAsync<TestEvent>(
            Messages(4),
            async (_, ct) =>
            {
                using var call = downstreamSource.StartActivity("downstream call", ActivityKind.Client);
                await Task.Delay(20, ct);
            },
            options: new RunTemporaryOptions { QueuePrefixName = prefix, PrefetchCount = 4 });

        // The run does not touch the caller's ambient activity.
        Assert.Same(root, Activity.Current);
        root.Stop();

        Assert.True(result.Success);
        Assert.Equal(4, downstream.Count);
        Assert.All(downstream, d => Assert.Equal(root.TraceId, d.TraceId));
        Assert.Equal(
            stopped.Select(s => s.SpanId.ToString()).OrderBy(id => id),
            downstream.Select(d => d.ParentSpanId.ToString()).OrderBy(id => id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PerMessageTimeout_IsATimeout_WhicheverCancellationExceptionTheHandlerThrows(bool throwsBaseType)
    {
        var prefix = $"trace-timeout-{Guid.NewGuid():N}";
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(prefix, stopped);

        var temporary = _fixture.BuildServiceProvider().GetRequiredService<IRabbitFlowTemporary>();

        var result = await temporary.RunAsync<TestEvent>(
            Messages(1),
            async (_, ct) =>
            {
                if (!throwsBaseType)
                {
                    // Task.Delay throws TaskCanceledException.
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    return;
                }

                // A handler that polls the token throws the base OperationCanceledException.
                while (true)
                {
                    await Task.Delay(20, CancellationToken.None);
                    ct.ThrowIfCancellationRequested();
                }
            },
            options: new RunTemporaryOptions { QueuePrefixName = prefix, Timeout = TimeSpan.FromMilliseconds(300) });

        Assert.Equal(1, result.FailedMessages);
        var error = Assert.Single(result.Errors);
        Assert.Equal(TemporaryRunErrorStage.Timeout, error.Stage);

        var span = Assert.Single(stopped);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(typeof(TimeoutException).FullName, span.GetTagItem("error.type"));
    }

    [Fact]
    public async Task FailedAndTimedOutMessages_SetErrorStatusOnTheirSpan()
    {
        var prefix = $"trace-errors-{Guid.NewGuid():N}";
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(prefix, stopped);

        var temporary = _fixture.BuildServiceProvider().GetRequiredService<IRabbitFlowTemporary>();
        var onErrorSpans = new ConcurrentDictionary<string, ActivitySpanId>();

        var result = await temporary.RunAsync<TestEvent>(
            new List<TestEvent>
            {
                new() { Id = "ok", Message = "ok" },
                new() { Id = "throws", Message = "throws" },
                new() { Id = "slow", Message = "slow" }
            },
            async (msg, ct) =>
            {
                // The handler tags its own span: Activity.Current is the message span.
                Activity.Current?.SetTag("test.id", msg.Id);
                if (msg.Id == "throws") throw new InvalidOperationException("boom");
                if (msg.Id == "slow") await Task.Delay(TimeSpan.FromSeconds(30), ct);
            },
            onError: (msg, _) =>
            {
                onErrorSpans[msg.Id] = Activity.Current!.SpanId;
                return Task.CompletedTask;
            },
            options: new RunTemporaryOptions { QueuePrefixName = prefix, PrefetchCount = 3, Timeout = TimeSpan.FromMilliseconds(500) });

        Assert.Equal(1, result.SucceededMessages);
        Assert.Equal(2, result.FailedMessages);
        Assert.Equal(3, stopped.Count);

        Activity Span(string id) => stopped.Single(s => (string?)s.GetTagItem("test.id") == id);

        Assert.Equal(ActivityStatusCode.Unset, Span("ok").Status);

        var failed = Span("throws");
        Assert.Equal(ActivityStatusCode.Error, failed.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, failed.GetTagItem("error.type"));
        Assert.Contains(failed.Events, e => e.Name == "exception");

        var timedOut = Span("slow");
        Assert.Equal(ActivityStatusCode.Error, timedOut.Status);
        Assert.Equal(typeof(TimeoutException).FullName, timedOut.GetTagItem("error.type"));

        // onError runs inside the span of the message it reports.
        Assert.Equal(failed.SpanId, onErrorSpans["throws"]);
        Assert.Equal(timedOut.SpanId, onErrorSpans["slow"]);
    }

    [Fact]
    public async Task CallerCancellation_SetsErrorStatus_AndKeepsTheRunResult()
    {
        var prefix = $"trace-cancel-{Guid.NewGuid():N}";
        var stopped = new ConcurrentBag<Activity>();
        using var listener = Listen(prefix, stopped);

        var temporary = _fixture.BuildServiceProvider().GetRequiredService<IRabbitFlowTemporary>();
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = temporary.RunAsync<TestEvent>(
            Messages(1),
            async (_, ct) =>
            {
                started.TrySetResult(true);
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            },
            options: new RunTemporaryOptions { QueuePrefixName = prefix },
            cancellationToken: cts.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        cts.Cancel();

        var result = await run;

        Assert.False(result.Success);
        Assert.Equal(1, result.FailedMessages);
        Assert.Contains(result.Errors, e => e.Stage == TemporaryRunErrorStage.Cancellation);

        var span = Assert.Single(stopped);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(typeof(TaskCanceledException).FullName, span.GetTagItem("error.type"));
    }

    [Fact]
    public async Task WithoutListener_RunsUnchanged_AndLinkedRootStillDetachesFromTheCaller()
    {
        var temporary = _fixture.BuildServiceProvider().GetRequiredService<IRabbitFlowTemporary>();

        using var root = StartRoot();

        // LinkedRoot first: clearing the ambient activity for its handlers must not reach the caller
        // nor the Child run that follows.
        var linkedSawActivity = true;
        var linkedResult = await temporary.RunAsync<TestEvent>(
            Messages(1),
            (_, _) => { linkedSawActivity = Activity.Current != null; return Task.CompletedTask; },
            options: new RunTemporaryOptions { TracingMode = TemporaryTracingMode.LinkedRoot });

        Assert.Same(root, Activity.Current);

        ActivityTraceId? childTrace = null;
        var childResult = await temporary.RunAsync<TestEvent>(
            Messages(1),
            (_, _) => { childTrace = Activity.Current?.TraceId; return Task.CompletedTask; });

        Assert.Same(root, Activity.Current);

        root.Stop();

        Assert.True(childResult.Success);
        Assert.True(linkedResult.Success);
        // No span is created; Child keeps the caller's ambient activity as before, LinkedRoot drops it.
        Assert.Equal(root.TraceId, childTrace);
        Assert.False(linkedSawActivity);
    }
}
