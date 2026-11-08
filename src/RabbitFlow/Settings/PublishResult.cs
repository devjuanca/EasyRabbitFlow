using System;
using System.Collections.Generic;

namespace EasyRabbitFlow.Settings
{
    /// <summary>
    /// Represents the outcome of a single message publish operation to RabbitMQ.
    /// Single-message publishes always use publisher confirms (<see cref="ChannelMode.Confirm"/>).
    /// </summary>
    public sealed class PublishResult
    {
        /// <summary>
        /// Gets whether the broker confirmed the publication and, when Mandatory is enabled, did not return it.
        /// This does not indicate consumer processing; by default it does not require routing to a queue.
        /// </summary>
        public bool Success { get; }

        /// <summary>
        /// Gets the identifier assigned to the published message. Always populated:
        /// either the value supplied by the caller via the <c>messageId</c> parameter (deterministic key for idempotency),
        /// or a unique GUID generated automatically when no value is supplied.
        /// </summary>
        public string MessageId { get; }

        /// <summary>
        /// Gets the destination exchange or queue name used for the publish operation.
        /// </summary>
        public string Destination { get; }

        /// <summary>
        /// Gets the routing key used for the publish operation.
        /// Empty when publishing directly to a queue.
        /// </summary>
        public string RoutingKey { get; }

        /// <summary>
        /// Gets the UTC timestamp when the publish operation was executed.
        /// </summary>
        public DateTime TimestampUtc { get; }

        /// <summary>
        /// Gets the exception that occurred during the publish operation, if any.
        /// <c>null</c> when <see cref="Success"/> is <c>true</c>.
        /// </summary>
        public Exception? Error { get; }

        private PublishResult(bool success, string destination, string routingKey, string messageId, Exception? error)
        {
            Success = success;
            Destination = destination;
            RoutingKey = routingKey;
            MessageId = messageId;
            TimestampUtc = DateTime.UtcNow;
            Error = error;
        }

        internal static PublishResult Successful(string destination, string routingKey, string messageId)
            => new PublishResult(true, destination, routingKey, messageId, null);

        internal static PublishResult Failed(string destination, string routingKey, string messageId, Exception error)
            => new PublishResult(false, destination, routingKey, messageId, error);
    }

    /// <summary>
    /// Represents the outcome of a batch publish operation to RabbitMQ.
    /// When <see cref="ChannelMode.Transactional"/> is used,
    /// messages are published within one AMQP transaction. A failed result can still include delivered messages,
    /// notably when mandatory returns are detected at commit or the commit outcome is unknown.
    /// </summary>
    public sealed class BatchPublishResult
    {
        /// <summary>
        /// Gets a value indicating whether all messages in the batch were published successfully.
        /// </summary>
        public bool Success { get; }

        /// <summary>
        /// Gets the destination exchange or queue name used for the batch publish operation.
        /// </summary>
        public string Destination { get; }

        /// <summary>
        /// Gets the routing key used for the batch publish operation.
        /// Empty when publishing directly to a queue.
        /// </summary>
        public string RoutingKey { get; }

        /// <summary>
        /// Gets the total number of messages included in the batch.
        /// </summary>
        public int MessageCount { get; }

        /// <summary>
        /// Gets the identifiers assigned to each message in the batch, in the same order as the input list.
        /// Contains IDs for publish calls that completed, using the selector or generated GUIDs.
        /// Transactional returns detected at commit leave all submitted IDs, including returned messages.
        /// This list is not a delivery receipt.
        /// </summary>
        public IReadOnlyList<string> MessageIds { get; }

        /// <summary>
        /// Gets the UTC timestamp when the batch publish operation was executed.
        /// </summary>
        public DateTime TimestampUtc { get; }

        /// <summary>
        /// Gets the <see cref="Settings.ChannelMode"/> used for the batch publish operation.
        /// </summary>
        public ChannelMode ChannelMode { get; }

        /// <summary>
        /// Gets the exception that caused the batch to fail, if any.
        /// When <see cref="ChannelMode.Transactional"/>, failures before commit attempt rollback.
        /// Returns detected at commit cannot undo routed messages. A failed or cancelled commit can have
        /// an unknown outcome; do not assume that no messages were delivered.
        /// <c>null</c> when <see cref="Success"/> is <c>true</c>.
        /// </summary>
        public Exception? Error { get; }

        private BatchPublishResult(bool success, string destination, string routingKey, ChannelMode channelMode, int messageCount, IReadOnlyList<string> messageIds, Exception? error)
        {
            Success = success;
            Destination = destination;
            RoutingKey = routingKey;
            ChannelMode = channelMode;
            MessageCount = messageCount;
            MessageIds = messageIds;
            TimestampUtc = DateTime.UtcNow;
            Error = error;
        }

        internal static BatchPublishResult Successful(string destination, string routingKey, ChannelMode channelMode, int messageCount, IReadOnlyList<string> messageIds)
            => new BatchPublishResult(true, destination, routingKey, channelMode, messageCount, messageIds, null);

        internal static BatchPublishResult Failed(string destination, string routingKey, ChannelMode channelMode, int messageCount, IReadOnlyList<string> messageIds, Exception error)
            => new BatchPublishResult(false, destination, routingKey, channelMode, messageCount, messageIds, error);
    }
}
