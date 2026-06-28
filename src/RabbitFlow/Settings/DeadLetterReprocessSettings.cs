using System;

namespace EasyRabbitFlow.Settings
{
    /// <summary>
    /// Configuration for the dead-letter reprocessor associated with a consumer.
    /// When enabled, a background service periodically drains the consumer's auto-generated dead-letter queue
    /// and re-publishes messages to the main queue until <see cref="MaxReprocessAttempts"/> is reached.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="ConsumerSettings{TConsumer}.AutoGenerate"/> to be <c>true</c>; otherwise the reprocessor is ignored.
    /// When the reprocessor is active, <see cref="ConsumerSettings{TConsumer}.ExtendDeadletterMessage"/> is forced to <c>true</c>
    /// because the reprocessor relies on the <see cref="DeadLetterEnvelope"/> wrapping to track attempt counts and recover the original payload.
    /// </remarks>
    /// <typeparam name="TConsumer">The consumer this configuration applies to.</typeparam>
    public class DeadLetterReprocessSettings<TConsumer> where TConsumer : class
    {
        /// <summary>
        /// Whether the reprocessor is enabled for this consumer. Default is <c>true</c>;
        /// the configuration only takes effect once <c>ConfigureDeadLetterReprocess</c> is called.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Maximum number of times a single message will be moved from the dead-letter queue back to the main queue.
        /// When this count is reached, the message is moved to the parking queue (<c>{queue}-deadletter-parking</c>)
        /// with the final attempt count recorded in its envelope so it can be inspected from a RabbitMQ client.
        /// Default is <c>3</c>.
        /// </summary>
        /// <remarks>
        /// This counts <b>re-enqueues</b>, not total handler executions. The original delivery is not a reprocess,
        /// so a value of <c>N</c> means the handler runs up to <c>N + 1</c> times before the message is parked
        /// (1 original delivery + <c>N</c> reprocesses). For example, <c>MaxReprocessAttempts = 1</c> ⇒ the message
        /// is delivered, fails, re-enqueued once, fails again, and is then parked: 2 executions in total.
        /// The minimum is <c>1</c> (an enabled reprocessor always grants at least one retry).
        /// </remarks>
        public int MaxReprocessAttempts
        {
            get => _maxReprocessAttempts;
            set => _maxReprocessAttempts = value < 1
                ? throw new ArgumentOutOfRangeException(nameof(MaxReprocessAttempts), "MaxReprocessAttempts must be greater than 0.")
                : value;
        }

        private int _maxReprocessAttempts = 3;

        /// <summary>
        /// Interval between reprocessor runs. The reprocessor is intended for slow, recovery-oriented retry of messages
        /// whose underlying failure takes a while to resolve — use the in-handler <c>RetryPolicy</c> for short retry windows.
        /// Default is 3 hours. <b>Minimum allowed is 10 minutes</b> and this floor cannot be lowered.
        /// </summary>
        public TimeSpan Interval
        {
            get => _interval;
            set => _interval = value < MinimumInterval
                ? throw new ArgumentOutOfRangeException(nameof(Interval), $"Interval must be at least {MinimumInterval.TotalMinutes} minute(s). Use the in-handler RetryPolicy for tighter retry cadences.")
                : value;
        }

        private TimeSpan _interval = TimeSpan.FromHours(3);

        /// <summary>
        /// Hard lower bound for <see cref="Interval"/>. Exposed as <c>readonly</c> so it cannot be lowered at runtime.
        /// </summary>
        public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Hard upper bound on the number of messages drained from the dead-letter queue in a single cycle.
        /// By default the cycle drains the whole queue snapshot taken at the start of the run; lower this value
        /// only if you need an explicit safety ceiling.
        /// </summary>
        /// <remarks>
        /// The cycle always takes a snapshot of the DLQ length at the start and never reprocesses messages it has
        /// just re-published within the same cycle, so this property is purely an additional safety cap — not
        /// required for correctness. Default is <see cref="int.MaxValue"/>.
        /// </remarks>
        public int MaxMessagesPerCycle
        {
            get => _maxMessagesPerCycle;
            set => _maxMessagesPerCycle = value < 1
                ? throw new ArgumentOutOfRangeException(nameof(MaxMessagesPerCycle), "MaxMessagesPerCycle must be greater than 0.")
                : value;
        }

        private int _maxMessagesPerCycle = int.MaxValue;

        /// <summary>
        /// What to do with a message that reaches a terminal, non-reprocessable state — <b>exhausted</b>
        /// (ran out of reprocess attempts) or <b>permanent</b> (non-transient failure). Default is
        /// <see cref="DeadLetterFinalAction.Park"/>, which preserves the historical behavior of moving the
        /// message to the parking queue.
        /// </summary>
        /// <remarks>
        /// This does <b>not</b> apply to <i>malformed</i> messages, which are always parked regardless of this
        /// value. When set to <see cref="DeadLetterFinalAction.Discard"/>, the parking queue is only ever
        /// created if a malformed message is encountered; exhausted and permanent messages are acknowledged
        /// off the dead-letter queue and dropped.
        /// </remarks>
        public DeadLetterFinalAction FinalAction { get; set; } = DeadLetterFinalAction.Park;

        /// <summary>
        /// Optional time-to-live applied to the parking queue <b>when the reprocessor creates it</b>. When set,
        /// the queue is declared with <c>x-message-ttl</c> so parked messages are aged out automatically after
        /// this duration. Default is <c>null</c> (no TTL — parked messages stay until consumed or purged).
        /// </summary>
        /// <remarks>
        /// The TTL only takes effect on a parking queue the reprocessor declares itself. If the queue already
        /// exists, the reprocessor adopts it as-is (it never fights over arguments), so a pre-existing parking
        /// queue keeps its own TTL — or lack of one. To apply this TTL to an existing queue, delete it and let
        /// the reprocessor recreate it. Note that a TTL with no dead-letter target on the parking queue means
        /// expired messages are dropped, so combining <see cref="DeadLetterFinalAction.Park"/> with a TTL is
        /// effectively a delayed discard.
        /// </remarks>
        public TimeSpan? ParkingMessageTtl
        {
            get => _parkingMessageTtl;
            set => _parkingMessageTtl = value.HasValue && value.Value <= TimeSpan.Zero
                ? throw new ArgumentOutOfRangeException(nameof(ParkingMessageTtl), "ParkingMessageTtl must be greater than zero when set.")
                : value;
        }

        private TimeSpan? _parkingMessageTtl;
    }
}
