namespace EasyRabbitFlow.Settings
{
    /// <summary>
    /// Snapshot of a queue's state, obtained in a single broker round trip.
    /// </summary>
    public sealed class QueueState
    {
        internal QueueState(string queueName, bool exists, uint messageCount, uint consumerCount)
        {
            QueueName = queueName;
            Exists = exists;
            MessageCount = messageCount;
            ConsumerCount = consumerCount;
        }

        /// <summary>
        /// Name of the inspected queue.
        /// </summary>
        public string QueueName { get; }

        /// <summary>
        /// Whether the queue exists on the broker. When <c>false</c>, all counts are zero.
        /// </summary>
        public bool Exists { get; }

        /// <summary>
        /// Number of messages ready in the queue (unacknowledged in-flight messages are not included).
        /// </summary>
        public uint MessageCount { get; }

        /// <summary>
        /// Number of consumers attached to the queue.
        /// </summary>
        public uint ConsumerCount { get; }

        /// <summary>
        /// True when the queue exists and holds no ready messages.
        /// </summary>
        public bool IsEmpty => Exists && MessageCount == 0;

        /// <summary>
        /// True when the queue exists and has at least one consumer attached.
        /// </summary>
        public bool HasConsumers => Exists && ConsumerCount > 0;
    }
}
