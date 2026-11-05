using System.Diagnostics;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Tests.Fixtures;
using EasyRabbitFlow.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EasyRabbitFlow.Tests;

[Collection("RabbitMq")]
public class TracingTests
{
    private readonly RabbitMqFixture _fixture;

    public TracingTests(RabbitMqFixture fixture)
    {
        _fixture = fixture;
    }

    private static ActivityListener CreateListener()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RabbitFlowDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };

        ActivitySource.AddActivityListener(listener);

        return listener;
    }

    [Fact]
    public async Task Publish_PropagatesTraceContext_To_Consumer()
    {
        // Arrange
        TraceCapturingConsumer.Reset();

        using var listener = CreateListener();

        var queueName = $"test-tracing-{Guid.NewGuid():N}";

        var sp = _fixture.BuildServiceProviderWithConsumers(settings =>
        {
            settings.AddConsumer<TraceCapturingConsumer>(queueName, cfg =>
            {
                cfg.AutoGenerate = true;
                cfg.PrefetchCount = 1;
                cfg.Timeout = TimeSpan.FromSeconds(10);
                cfg.ConfigureAutoGenerate(ag =>
                {
                    ag.GenerateExchange = false;
                    ag.GenerateDeadletterQueue = false;
                    ag.DurableQueue = true;
                    ag.AutoDeleteQueue = true;
                });
            });
        });

        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();

        // Act – publish inside an ambient activity, like an ASP.NET Core request
        using var rootActivity = new Activity("test-root");
        rootActivity.SetIdFormat(ActivityIdFormat.W3C);
        rootActivity.Start();

        var expectedTraceId = rootActivity.TraceId.ToString();

        var result = await publisher.PublishAsync(new TestEvent { Id = "trace-1", Message = "traced" }, queueName);

        rootActivity.Stop();

        Assert.True(result.Success);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (TraceCapturingConsumer.Received.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        // Assert – the traceparent header arrived and the consumer-side activity continued the same trace
        var captured = Assert.Single(TraceCapturingConsumer.Received);
        Assert.NotNull(captured.TraceParentHeader);
        Assert.Contains(expectedTraceId, captured.TraceParentHeader);
        Assert.Equal(expectedTraceId, captured.ActivityTraceId);

        await hostedService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ProcessSpan_OnHandlerFailure_CarriesErrorTypeAndExceptionEvent()
    {
        // A failed delivery must show up in tracing backends as a failed span with the exception attached:
        // error status, error.type and an "exception" event, plus the delivery tag and operation attributes.
        AlwaysFailConsumer.Reset();

        var stopped = new System.Collections.Concurrent.ConcurrentBag<Activity>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RabbitFlowDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stopped.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var queueName = $"test-tracing-fail-{Guid.NewGuid():N}";

        var sp = _fixture.BuildServiceProviderWithConsumers(settings =>
        {
            settings.AddConsumer<AlwaysFailConsumer>(queueName, cfg =>
            {
                cfg.AutoGenerate = true;
                cfg.Timeout = TimeSpan.FromSeconds(10);
                cfg.ConfigureAutoGenerate(ag =>
                {
                    ag.GenerateExchange = false;
                    ag.GenerateDeadletterQueue = true;
                    ag.DurableQueue = true;
                    ag.AutoDeleteQueue = false;
                });
            });
        });

        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();
        await publisher.PublishAsync(new TestEvent { Id = "span-fail", Message = "fails" }, queueName, messageId: "msg-span-1");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!stopped.Any(a => a.DisplayName == $"{queueName} process") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        var process = stopped.FirstOrDefault(a => a.DisplayName == $"{queueName} process");

        Assert.NotNull(process);
        Assert.Equal(ActivityStatusCode.Error, process!.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, process.GetTagItem("error.type"));
        Assert.Equal("process", process.GetTagItem("messaging.operation.type"));
        Assert.Equal("msg-span-1", process.GetTagItem("messaging.message.id"));
        Assert.NotNull(process.GetTagItem("messaging.rabbitmq.message.delivery_tag"));

        var exceptionEvent = process.Events.FirstOrDefault(e => e.Name == "exception");
        Assert.NotEqual(default, exceptionEvent);
        Assert.Contains(exceptionEvent.Tags, t => t.Key == "exception.type" && (string?)t.Value == typeof(InvalidOperationException).FullName);
        Assert.Contains(exceptionEvent.Tags, t => t.Key == "exception.stacktrace");

        var publish = stopped.FirstOrDefault(a => a.DisplayName == $"{queueName} publish");
        Assert.NotNull(publish);
        Assert.Equal("send", publish!.GetTagItem("messaging.operation.type"));

        await hostedService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Publish_WithoutAmbientActivity_DoesNotInjectHeaders()
    {
        // Arrange
        TraceCapturingConsumer.Reset();

        var queueName = $"test-tracing-off-{Guid.NewGuid():N}";

        var sp = _fixture.BuildServiceProviderWithConsumers(settings =>
        {
            settings.AddConsumer<TraceCapturingConsumer>(queueName, cfg =>
            {
                cfg.AutoGenerate = true;
                cfg.PrefetchCount = 1;
                cfg.Timeout = TimeSpan.FromSeconds(10);
                cfg.ConfigureAutoGenerate(ag =>
                {
                    ag.GenerateExchange = false;
                    ag.GenerateDeadletterQueue = false;
                    ag.DurableQueue = true;
                    ag.AutoDeleteQueue = true;
                });
            });
        });

        var hostedService = sp.GetServices<IHostedService>().First();
        await hostedService.StartAsync(CancellationToken.None);
        await Task.Delay(500);

        var publisher = sp.GetRequiredService<IRabbitFlowPublisher>();

        // Act – no listener and no ambient activity: publishing must behave exactly as before
        Activity.Current = null;

        var result = await publisher.PublishAsync(new TestEvent { Id = "no-trace", Message = "plain" }, queueName);

        Assert.True(result.Success);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (TraceCapturingConsumer.Received.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        // Assert
        var captured = Assert.Single(TraceCapturingConsumer.Received);
        Assert.Null(captured.TraceParentHeader);

        await hostedService.StopAsync(CancellationToken.None);
    }
}
