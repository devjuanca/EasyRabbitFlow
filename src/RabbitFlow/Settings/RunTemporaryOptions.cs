using System;

namespace EasyRabbitFlow.Settings
{
    /// <summary>
    /// Configuration options for executing a temporary RabbitMQ message flow.
    /// </summary>
    public class RunTemporaryOptions
    {
        /// <summary>
        /// A custom correlation ID used for logging and tracing the execution flow.
        /// </summary>
        public string? CorrelationId { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// The number of unacknowledged messages that can be pre-fetched by the consumer at a time.
        /// Default is 1.
        /// </summary>
        public ushort PrefetchCount
        {
            get => _prefetchCount;
            set => _prefetchCount = value == 0 ? throw new ArgumentOutOfRangeException(nameof(PrefetchCount), "PrefetchCount must be greater than 0.") : value;
        }

        private ushort _prefetchCount = 1;

        /// <summary>
        /// Optional timeout duration applied to the processing of each individual message.
        /// If the handler does not complete within the timeout, it is treated as a failed message.
        /// </summary>
        public TimeSpan? Timeout
        {
            get => _timeout;
            set
            {
                if (value.HasValue && value.Value < TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(Timeout), "Timeout must not be negative.");
                }
                _timeout = value;
            }
        }

        private TimeSpan? _timeout;

        /// <summary>
        /// Optional timeout for the whole run, acting as a safety net against runs that can never finish
        /// (e.g. messages lost to a broker failure). When it elapses, in-progress handlers are canceled
        /// cooperatively, pending messages are reported as failed, and the run completes with the partial result.
        /// Unlike <see cref="Timeout"/>, which applies per message, this bounds the total duration of the run.
        /// </summary>
        public TimeSpan? RunTimeout
        {
            get => _runTimeout;
            set
            {
                if (value.HasValue && value.Value <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(RunTimeout), "RunTimeout must be greater than zero.");
                }
                _runTimeout = value;
            }
        }

        private TimeSpan? _runTimeout;

        /// <summary>
        /// Optional prefix used to customize the generated queue name.
        /// </summary>
        public string? QueuePrefixName { get; set; }

        public static RunTemporaryOptions Default => new RunTemporaryOptions();

    }
}