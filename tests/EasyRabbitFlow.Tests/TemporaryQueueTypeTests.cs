using EasyRabbitFlow.Services;
using EasyRabbitFlow.Tests.Fixtures;
using EasyRabbitFlow.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace EasyRabbitFlow.Tests;

[Collection("RabbitMq")]
public class TemporaryQueueTypeTests
{
    private readonly RabbitMqFixture _fixture;

    public TemporaryQueueTypeTests(RabbitMqFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RunAsync_TemporaryQueue_IsAlwaysClassic_EvenOnAQuorumByDefaultBroker()
    {
        // The temporary queue is declared exclusive + non-durable + auto-delete without x-queue-type. A quorum queue
        // can be neither exclusive nor non-durable, so a broker whose default_queue_type is quorum must still create
        // it as a classic queue (and not reject the declare). Probed while a handler holds the run open.
        using var sp = (ServiceProvider)_fixture.BuildServiceProvider();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = sp.GetRequiredService<IRabbitFlowTemporary>().RunAsync(
            new List<TestEvent> { new() { Id = "1", Message = "1" } },
            async (_, ct) =>
            {
                started.TrySetResult(true);
                await release.Task.WaitAsync(ct);
            },
            cancellationToken: deadline.Token);

        await started.Task.WaitAsync(deadline.Token);

        var queues = await _fixture.DumpQueuesAsync("-temp-queue-");

        release.TrySetResult(true);
        var result = await run.WaitAsync(deadline.Token);

        Assert.Equal(1, result.SucceededMessages);
        Assert.False(string.IsNullOrWhiteSpace(queues), "The temporary queue was not visible while the run was open.");
        Assert.Contains("classic", queues);
        Assert.DoesNotContain("quorum", queues);
    }
}
