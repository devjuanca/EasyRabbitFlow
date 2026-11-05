using EasyRabbitFlow.Settings;

namespace EasyRabbitFlow.Tests;

public class ConsumerTimeoutRulesTests
{
    [Fact]
    public void Default_30s_NoRetries_IsFlooredAtOneMinute()
    {
        // 30s × 1 attempt + 30s grace = 60s, exactly the broker minimum.
        var ms = ConsumerTimeoutRules.ServerTimeoutMs(TimeSpan.FromSeconds(30), totalAttempts: 1, retryIntervalMs: 0);

        Assert.Equal(60_000L, ms);
    }

    [Fact]
    public void ShortTimeout_NoRetries_IsFlooredAtOneMinute()
    {
        var ms = ConsumerTimeoutRules.ServerTimeoutMs(TimeSpan.FromSeconds(5), totalAttempts: 1, retryIntervalMs: 0);

        Assert.Equal(60_000L, ms);
    }

    [Fact]
    public void CoversEveryAttempt_EveryRetryDelay_AndTheGrace()
    {
        // 10 min × 3 attempts + 5s × 2 retries + 30s grace = 1 840 000 ms.
        var ms = ConsumerTimeoutRules.ServerTimeoutMs(TimeSpan.FromMinutes(10), totalAttempts: 3, retryIntervalMs: 5_000);

        Assert.Equal(1_840_000L, ms);
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

        // One attempt, no retry delay: 120s + 30s.
        Assert.Equal(150_000L, ms);
    }

    [Fact]
    public void HugeTimeout_SaturatesInsteadOfWrapping()
    {
        var ms = ConsumerTimeoutRules.ServerTimeoutMs(TimeSpan.MaxValue, totalAttempts: int.MaxValue, retryIntervalMs: int.MaxValue);

        Assert.Equal(long.MaxValue, ms);
    }
}
