namespace RabbitFlow.Settings
{
    /// <summary>
    /// Represents options for configuring the behavior of the RabbitFlow publisher service.
    /// </summary>
    public class PublisherOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether the publisher connection to RabbitMQ should be disposed after usage.
        /// When set to <c>true</c>, the connection will be disposed after each use, allowing for resource cleanup.
        /// If set to <c>false</c>, the connection will be kept open for potential reuse.
        /// </summary>

        public bool DisposePublisherConnection { get; set; } = false;
    }
}
