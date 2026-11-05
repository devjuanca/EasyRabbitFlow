using System;

namespace EasyRabbitFlow.Settings
{
    // Derives the broker-side delivery acknowledgement timeout (queue argument 'x-consumer-timeout') from the
    // consumer's handler timeout and retry policy. The in-process retry cycle holds the delivery unacknowledged
    // for its whole duration, so the broker window must cover every attempt, every wait between attempts and
    // the watchdog grace the handler gets after its last attempt is cancelled.
    internal static class ConsumerTimeoutRules
    {
        // RabbitMQ rejects consumer timeouts below one minute.
        internal const long MinimumServerTimeoutMs = 60_000;

        // Extra window on top of the computed cycle, matching the stuck-handler abandon grace.
        internal const long ServerTimeoutGraceMs = 30_000;

        /// <summary>
        /// Milliseconds to declare as <c>x-consumer-timeout</c>, or <c>null</c> when the handler has no timeout
        /// (<paramref name="handlerTimeout"/> non-positive, e.g. <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>):
        /// a handler allowed to run indefinitely must not be cut off by a finite broker timeout.
        /// </summary>
        internal static long? ServerTimeoutMs(TimeSpan handlerTimeout, int totalAttempts, int retryIntervalMs)
        {
            if (handlerTimeout <= TimeSpan.Zero)
            {
                return null;
            }

            var attempts = Math.Max(1, totalAttempts);

            var retries = attempts - 1;

            // Computed in floating point so absurd inputs saturate instead of wrapping around to a negative value.
            var computed = handlerTimeout.TotalMilliseconds * attempts
                           + (double)Math.Max(0, retryIntervalMs) * retries
                           + ServerTimeoutGraceMs;

            if (computed >= long.MaxValue)
            {
                return long.MaxValue;
            }

            return Math.Max(MinimumServerTimeoutMs, (long)computed);
        }
    }
}
