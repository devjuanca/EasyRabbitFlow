namespace EasyRabbitFlow.Settings
{
    /// <summary>
    /// Represents the retry policy settings for handling message processing retries in the RabbitFlow consumer.
    /// This class provides configuration options for controlling how the system should behave when message processing fails:
    /// the number of retry attempts and the fixed interval between them. Retries apply only to transient failures and run
    /// in-process while the delivery stays unacknowledged, so this policy is intended for short, ephemeral recovery only.
    /// </summary>
    /// <typeparam name="TConsumer">The type of the consumer that this retry policy applies to.</typeparam>
    public class RetryPolicy<TConsumer> where TConsumer : class
    {
        /// <summary>
        /// Gets or sets the maximum number of retry attempts performed after the initial processing attempt fails.
        /// <para>
        /// <c>0</c> = no retries (the message is processed once and not retried after a failure).<br/>
        /// <c>1</c> = one retry after the first failure (up to 2 total attempts).<br/>
        /// <c>3</c> = three retries after the first failure (up to 4 total attempts).
        /// </para>
        /// The default value is 0.
        /// </summary>
        public int MaxRetryCount { get; set; } = 0;

        /// <summary>
        /// Gets or sets the fixed time interval (in milliseconds) waited between retry attempts.
        /// The delay is constant across all retries by design: the in-handler retry policy is meant for
        /// short, ephemeral transient failures (a brief network blip, a momentary 5xx, a transient deadlock).
        /// Keep this small. For failures that take minutes to resolve, use the dead-letter reprocessor instead,
        /// which retries on a slow recovery cadence without holding the delivery unacknowledged.
        /// The default value is 1000 milliseconds (1 second).
        /// </summary>
        public int RetryInterval { get; set; } = 1000;
    }
}
