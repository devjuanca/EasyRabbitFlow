namespace RabbitFlow.Settings
{
    /// <summary>
    /// Represents options for configuring the behavior of the RabbitFlow publisher service.
    /// This class provides configuration for how the publisher manages its connection to RabbitMQ.
    /// </summary>
    public class PublisherOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether the publisher connection to RabbitMQ should be disposed after each usage.
        /// When set to <c>true</c>, the connection will be disposed after each publishing operation,
        /// which helps in freeing up resources but may result in the overhead of re-establishing connections.
        /// If set to <c>false</c>, the connection remains open for reuse, which can improve performance in high-throughput scenarios.
        /// Default value is <c>false</c>.
        /// </summary>

        public bool DisposePublisherConnection { get; set; } = false;
    }
}
