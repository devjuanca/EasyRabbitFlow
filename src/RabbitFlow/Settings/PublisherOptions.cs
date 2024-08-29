namespace EasyRabbitFlow.Settings
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


        /// <summary>
        /// Gets or sets the mode in which the RabbitMQ channel will operate.
        /// This property determines how messages are confirmed after being published.
        /// <list type="bullet">
        /// <item><c>Transactional</c>: The channel will operate in transactional mode, where messages are only considered published after a transaction is committed. This ensures reliability but can introduce performance overhead.</item>
        /// <item><c>Confirm</c>: The channel will use publisher confirms, where the broker sends a confirmation to indicate that the message was received. This mode offers a balance between reliability and performance.</item>
        /// </list>
        /// Default value is <c>Confirm</c>.
        /// </summary>
        public ChannelMode ChannelMode { get; set; } = ChannelMode.Confirm;
    }

    /// <summary>
    /// Specifies the mode in which the RabbitMQ channel operates for message publishing.
    /// </summary>
    public enum ChannelMode
    {
        Transactional,
        Confirm
    }
}
