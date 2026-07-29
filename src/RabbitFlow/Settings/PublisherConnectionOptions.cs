namespace EasyRabbitFlow.Settings
{
    /// <summary>
    /// Represents options for configuring the behavior of the RabbitFlow publisher service.
    /// This class provides configuration for how the publisher manages its connection to RabbitMQ.
    /// </summary>
    public class PublisherConnectionOptions
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
        /// Label appended to the publisher connection name as <c>"Publisher_{PublisherId}"</c> in the RabbitMQ
        /// management UI and broker logs. Purely cosmetic — it does not affect routing, identity, or connection pooling.
        /// Default is empty (connection name is <c>"Publisher_"</c>).
        /// </summary>
        public string PublisherId { get; set; } = "";

        /// <summary>
        /// Maximum number of confirm-channels kept open for reuse by single-message publishes.
        /// The pool serves that many concurrent publishes without opening a new channel; publishes beyond it
        /// create a channel on demand and dispose it on return, so this is a reuse cap, not a concurrency limit.
        /// Size it to the expected number of concurrent single-message publishes: each pooled channel handles one
        /// publish at a time, and a publish+confirm round-trip typically takes single-digit milliseconds. Values
        /// below 1 are treated as 1. Ignored when <see cref="DisposePublisherConnection"/> is <c>true</c> (channels
        /// cannot outlive the per-publish connection, so nothing is pooled).
        /// Default is 8, which saturates moderate concurrency (≤16 concurrent publishes). For sustained
        /// high-concurrency fan-outs of single-message publishes, raise it toward the expected concurrency
        /// (around 32 is a good ceiling: all channels share one connection, so far larger pools add contention
        /// instead of throughput). Batch publishes (<c>PublishBatchAsync</c>) never use the pool — each batch
        /// opens its own channel — so batch-heavy workloads gain nothing from raising this.
        /// The pool grows on demand up to the cap, so a higher value costs nothing until bursts actually occur.
        /// </summary>
        public int MaxPooledChannels { get; set; } = 8;
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
