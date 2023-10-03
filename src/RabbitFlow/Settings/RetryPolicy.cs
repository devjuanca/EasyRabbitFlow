namespace RabbitFlow.Settings
{
    /// <summary>
    /// Represents the retry policy settings for handling message processing retries.
    /// </summary>
    public class RetryPolicy<TConsumer>
    {
        /// <summary>
        /// Gets or sets the maximum number of retry attempts. Default is 1.
        /// </summary>
        public int MaxRetryCount { get; set; } = 1;

        /// <summary>
        /// Gets or sets the time interval (in milliseconds) between retry attempts. Default is 1000.
        /// </summary>
        public int RetryInterval { get; set; } = 1000;

        /// <summary>
        /// Gets or sets a value indicating whether to use exponential backoff for retry intervals. Default is false.
        /// </summary>
        public bool ExponentialBackoff { get; set; } = false;

        /// <summary>
        /// Gets or sets the factor by which to multiply the retry interval for each exponential backoff attempt. Default is 1.
        /// </summary>
        public int ExponentialBackoffFactor { get; set; } = 1;
    }
}