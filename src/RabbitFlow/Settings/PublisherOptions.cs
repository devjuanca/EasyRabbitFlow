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
        /// Default value is false.
        /// </summary>

        public bool DisposePublisherConnection { get; set; } = false;


        /// <summary>
        /// Gets or sets a value indicating whether idempotency support is enabled for published messages.
        /// When <c>true</c>, the publisher automatically assigns a unique <c>MessageId</c> to each message
        /// via <see cref="RabbitMQ.Client.IBasicProperties.MessageId"/>. Consumers can use this value 
        /// for deduplication. The generated <c>MessageId</c> is also available in <see cref="PublishResult.MessageId"/>.
        /// Default value is <c>false</c>.
        /// </summary>
        public bool IdempotencyEnabled { get; set; } = false;
    }

    /// <summary>
    /// Specifies the mode in which the RabbitMQ channel operates for batch message publishing.
    /// </summary>
    public enum ChannelMode
    {
        /// <summary>
        /// All messages in the batch are published atomically within a single AMQP transaction.
        /// If any message fails, the entire batch is rolled back and no messages are delivered.
        /// </summary>
        Transactional,

        /// <summary>
        /// Each message in the batch is individually confirmed by the broker.
        /// A failure mid-batch does not roll back previously confirmed messages.
        /// </summary>
        Confirm
    }
}
