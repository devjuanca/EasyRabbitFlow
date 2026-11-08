using EasyRabbitFlow.Settings;

namespace EasyRabbitFlow.Tests;

public class ConsumerTimeoutRulesTests
{
    [Fact]
    public void Default_30s_NoRetries_IsTimeoutPlusGracePlusMargin()
    {
        // (30s + 30s grace) × 1 attempt + 10s dead-letter margin = 70s.
        var ms = ConsumerTimeoutRules.ServerTimeoutMs(TimeSpan.FromSeconds(30), totalAttempts: 1, retryIntervalMs: 0);

        Assert.Equal(70_000L, ms);
    }

    [Fact]
    public void TwentySeconds_NoRetries_LandsExactlyOnTheFloor()
    {
        // (20s + 30s grace) × 1 attempt + 10s margin = 60s, exactly the broker minimum.
        var ms = ConsumerTimeoutRules.ServerTimeoutMs(TimeSpan.FromSeconds(20), totalAttempts: 1, retryIntervalMs: 0);

        Assert.Equal(60_000L, ms);
    }

    [Fact]
    public void ShortTimeout_NoRetries_IsFlooredAtOneMinute()
    {
        var ms = ConsumerTimeoutRules.ServerTimeoutMs(TimeSpan.FromSeconds(5), totalAttempts: 1, retryIntervalMs: 0);

        Assert.Equal(60_000L, ms);
    }

    [Fact]
    public void CoversEveryAttempt_EveryRetryDelay_AndTheGraceOfEachAttempt()
    {
        // (10 min + 30s grace) × 3 attempts + 5s × 2 retries + 10s margin = 1 910 000 ms.
        var ms = ConsumerTimeoutRules.ServerTimeoutMs(TimeSpan.FromMinutes(10), totalAttempts: 3, retryIntervalMs: 5_000);

        Assert.Equal(1_910_000L, ms);
    }

    [Fact]
    public void SlowCooperativeCancellation_OnEveryAttempt_StaysInsideTheWindow()
    {
        // Each attempt may legitimately last Timeout + grace: the handler sees its token cancelled at Timeout,
        // unwinds just before the watchdog abandons it, and the resulting OperationCanceledException is retried
        // as a transient timeout. Three such attempts hold the delivery unacknowledged for 3 × (Timeout + grace);
        // the broker window must not expire before the last one ends.
        // (50s + 30s) × 3 attempts = 240s of handler time, plus the 10s dead-letter margin.
        var ms = ConsumerTimeoutRules.ServerTimeoutMs(TimeSpan.FromSeconds(50), totalAttempts: 3, retryIntervalMs: 0);

        Assert.Equal(250_000L, ms);
    }

    [Theory]
    [InlineData(-1)]      // Timeout.InfiniteTimeSpan is -1 ms
    [InlineData(0)]
    [InlineData(-5000)]
    public void NonPositiveHandlerTimeout_MeansNoServerTimeout(int milliseconds)
    {
        // A handler allowed to run indefinitely must not be cut off by a 60s broker timeout: before this rule the
        // negative value flowed into the formula and the floor turned it into exactly one minute.
        var ms = ConsumerTimeoutRules.ServerTimeoutMs(TimeSpan.FromMilliseconds(milliseconds), totalAttempts: 1, retryIntervalMs: 0);

        Assert.Null(ms);
    }

    [Fact]
    public void InfiniteTimeSpan_MeansNoServerTimeout()
    {
        Assert.Null(ConsumerTimeoutRules.ServerTimeoutMs(Timeout.InfiniteTimeSpan, totalAttempts: 4, retryIntervalMs: 10_000));
    }

    [Fact]
    public void ZeroOrNegativeAttempts_CountAsOne()
    {
        var ms = ConsumerTimeoutRules.ServerTimeoutMs(TimeSpan.FromMinutes(2), totalAttempts: 0, retryIntervalMs: 60_000);

        // One attempt, no retry delay: 120s + 30s grace + 10s margin.
        Assert.Equal(160_000L, ms);
    }

    [Fact]
    public void HugeTimeout_SaturatesInsteadOfWrapping()
    {
        var ms = ConsumerTimeoutRules.ServerTimeoutMs(TimeSpan.MaxValue, totalAttempts: int.MaxValue, retryIntervalMs: int.MaxValue);

        Assert.Equal(long.MaxValue, ms);
    }
}
