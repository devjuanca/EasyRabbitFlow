using EasyRabbitFlow.Services;

namespace EasyRabbitFlow.Tests;

public class ConsumerRecoveryGateTests
{
    [Fact]
    public void RequestThatArrivesAfterTheLastCheck_IsReportedToTheHolderOnExit()
    {
        // A finished its pass and found nothing pending, B arrived while A still held the gate (so B only recorded
        // its request), then A left. Exit must report that request: otherwise nobody runs again and the consumer
        // stays unsubscribed until the next broker event.
        using var gate = new ConsumerRecoveryGate();

        Assert.True(gate.TryEnter());
        gate.BeginPass();
        Assert.False(gate.TakePending());

        Assert.False(gate.TryEnter());

        Assert.True(gate.Exit());

        // The re-triggered pass gets the gate and has nothing further to do.
        Assert.True(gate.TryEnter());
        gate.BeginPass();
        Assert.False(gate.TakePending());
        Assert.False(gate.Exit());
    }

    [Fact]
    public void RequestThatArrivesDuringAPass_MakesTheHolderGoRoundAgain()
    {
        using var gate = new ConsumerRecoveryGate();

        Assert.True(gate.TryEnter());
        gate.BeginPass();

        Assert.False(gate.TryEnter());

        Assert.True(gate.TakePending());
        gate.BeginPass();
        Assert.False(gate.TakePending());
        Assert.False(gate.Exit());
    }

    [Fact]
    public void RequestRecordedBeforeAPassStarts_IsSatisfiedByThatPass()
    {
        using var gate = new ConsumerRecoveryGate();

        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());

        gate.BeginPass();

        Assert.False(gate.TakePending());
        Assert.False(gate.Exit());
    }

    [Fact]
    public void Exit_WithoutRequests_DoesNotAskForAnotherPass()
    {
        using var gate = new ConsumerRecoveryGate();

        Assert.True(gate.TryEnter());
        gate.BeginPass();

        Assert.False(gate.Exit());
        Assert.True(gate.TryEnter());
        Assert.False(gate.Exit());
    }

    [Fact]
    public async Task ConcurrentOneShotRequests_NeverLeaveARecordedRequestWithoutAPass()
    {
        // Each worker is one broker event: it tries the gate exactly once, as the consumer does, and if it fails it
        // relies on the holder. Whatever the interleaving, once everybody is done the gate must be free and no
        // request may be left recorded: a set flag on a free gate is a request nobody will ever honour.
        for (var round = 0; round < 20_000; round++)
        {
            using var gate = new ConsumerRecoveryGate();
            using var start = new ManualResetEventSlim(false);

            var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                start.Wait();

                var runAgain = gate.TryEnter();

                while (runAgain)
                {
                    do
                    {
                        gate.BeginPass();
                    }
                    while (gate.TakePending());

                    runAgain = gate.Exit() && gate.TryEnter();
                }
            })).ToArray();

            start.Set();

            await Task.WhenAll(workers);

            Assert.True(gate.TryEnter(), $"round {round}: the gate was left held");
            Assert.False(gate.TakePending(), $"round {round}: a recorded request was left without a pass");
        }
    }

    [Fact]
    public async Task WaitAsync_GivesUp_WhenTheHolderDoesNotLeaveInTime()
    {
        using var gate = new ConsumerRecoveryGate();

        Assert.True(gate.TryEnter());

        Assert.False(await gate.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));

        Assert.False(gate.Exit());

        Assert.True(await gate.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
        gate.Release();
    }

    [Fact]
    public async Task WaitAsync_HonoursTheCallerToken()
    {
        using var gate = new ConsumerRecoveryGate();
        using var cts = new CancellationTokenSource(50);

        Assert.True(gate.TryEnter());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.WaitAsync(TimeSpan.FromSeconds(10), cts.Token));

        Assert.False(gate.Exit());
    }
}
