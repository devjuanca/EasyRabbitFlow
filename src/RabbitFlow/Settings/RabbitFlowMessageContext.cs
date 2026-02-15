using System.Collections.Generic;

namespace EasyRabbitFlow.Settings
{
    /// <summary>
    /// Provides access to AMQP metadata of the message currently being processed by a consumer.
    /// Passed as a parameter to <c>HandleAsync</c> alongside the deserialized message body.
    /// </summary>
    public sealed class RabbitFlowMessageContext
    {
        internal RabbitFlowMessageContext(
            string? messageId,
            string? correlationId,
            string? exchange,
            string? routingKey,
            IDictionary<string, object?>? headers,
            ulong deliveryTag,
            bool redelivered)
        {
            MessageId = messageId;
            CorrelationId = correlationId;
            Exchange = exchange;
            RoutingKey = routingKey;
            Headers = headers;
            DeliveryTag = deliveryTag;
            Redelivered = redelivered;
        }

        /// <summary>
        /// Gets the <c>MessageId</c> from <c>BasicProperties</c>.
        /// When the publisher has <see cref="PublisherOptions.IdempotencyEnabled"/> set to <c>true</c>,
        /// this contains the unique identifier assigned to the message for deduplication.
        /// <c>null</c> when the publisher did not set a <c>MessageId</c>.
        /// </summary>
        public string? MessageId { get; }

        /// <summary>
        /// Gets the <c>CorrelationId</c> from <c>BasicProperties</c>.
        /// Useful for tracing request/reply flows or correlating related messages.
        /// </summary>
        public string? CorrelationId { get; }

        /// <summary>
        /// Gets the exchange that delivered the message.
        /// Empty string when the message was published directly to a queue.
        /// </summary>
        public string? Exchange { get; }

        /// <summary>
        /// Gets the routing key used when the message was published.
        /// </summary>
        public string? RoutingKey { get; }

        /// <summary>
        /// Gets the AMQP headers from <c>BasicProperties</c>.
        /// <c>null</c> when no custom headers were set by the publisher.
        /// </summary>
        public IDictionary<string, object?>? Headers { get; }

        /// <summary>
        /// Gets the delivery tag assigned by the broker for this message.
        /// </summary>
        public ulong DeliveryTag { get; }

        /// <summary>
        /// Gets a value indicating whether this message was redelivered by the broker.
        /// </summary>
        public bool Redelivered { get; }
    }
}
