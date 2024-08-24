namespace RabbitFlow.Settings
{
    /// <summary>
    /// Represents the retry policy settings for handling message processing retries in the RabbitFlow consumer.
    /// This class provides configuration options for controlling how the system should behave when message processing fails,
    /// including the number of retry attempts, intervals between retries, and optional use of exponential backoff.
    /// </summary>
    /// <typeparam name="TConsumer">The type of the consumer that this retry policy applies to.</typeparam>
    public class RetryPolicy<TConsumer> where TConsumer : class
    {
        /// <summary>
        /// Gets or sets the maximum number of retry attempts. 
        /// The default value is 1, meaning there will be one retry attempt after the initial failure.
        /// </summary>
        public int MaxRetryCount { get; set; } = 1;

        /// <summary>
        /// Gets or sets the time interval (in milliseconds) between retry attempts.
        /// The default value is 1000 milliseconds (1 second).
        /// </summary>
        public int RetryInterval { get; set; } = 1000;

        /// <summary>
        /// Gets or sets a value indicating whether to use exponential backoff for retry intervals.
        /// If set to <c>true</c>, the retry interval will increase exponentially with each retry attempt.
        /// The default value is <c>false</c>, meaning the retry interval remains constant.
        /// </summary>
        public bool ExponentialBackoff { get; set; } = false;

        /// <summary>
        /// Gets or sets the factor by which to multiply the retry interval for each exponential backoff attempt.
        /// This value is used only if <see cref="ExponentialBackoff"/> is set to <c>true</c>.
        /// The default value is 1, meaning the retry interval will act linear.
        /// </summary>
        public int ExponentialBackoffFactor { get; set; } = 1;
    }
}