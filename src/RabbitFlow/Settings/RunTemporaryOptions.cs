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
        public ushort PrefetchCount { get; set; } = 1;

        /// <summary>
        /// Optional timeout duration applied to the processing of each individual message.
        /// If the handler does not complete within the timeout, it is treated as a failed message.
        /// </summary>
        public TimeSpan? Timeout { get; set; }

        /// <summary>
        /// Optional prefix used to customize the generated queue name.
        /// </summary>
        public string? QueuePrefixName { get; set; }
    }
}