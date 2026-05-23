using System;
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
            bool redelivered,
            int reprocessAttempts,
            MessageDeliveryMode? deliveryMode,
            string? type,
            string? appId,
            TimeSpan? expiration,
            byte? priority,
            DateTimeOffset? timestamp,
            string? replyTo,
            string? contentType)
        {
            MessageId = messageId;
            CorrelationId = correlationId;
            Exchange = exchange;
            RoutingKey = routingKey;
            Headers = headers;
            DeliveryTag = deliveryTag;
            Redelivered = redelivered;
            ReprocessAttempts = reprocessAttempts;
            DeliveryMode = deliveryMode;
            Type = type;
            AppId = appId;
            Expiration = expiration;
            Priority = priority;
            Timestamp = timestamp;
            ReplyTo = replyTo;
            ContentType = contentType;
        }

        /// <summary>
        /// Gets the <c>MessageId</c> from <c>BasicProperties</c>. Always populated when the message was published
        /// via <see cref="Services.IRabbitFlowPublisher"/> (either the deterministic key supplied by the caller
        /// or an auto-generated GUID). May be <c>null</c> only if the message originated from a third-party publisher
        /// that did not set <c>BasicProperties.MessageId</c>.
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

        /// <summary>
        /// Number of times this message has been re-enqueued from the dead-letter queue back to the main queue
        /// by the dead-letter reprocessor. <c>0</c> for messages that have not been reprocessed.
        /// Sourced from the <c>x-reprocess-attempts</c> AMQP header.
        /// </summary>
        public int ReprocessAttempts { get; }

        /// <summary>
        /// AMQP <c>delivery-mode</c>. <c>null</c> when the publisher did not set it explicitly
        /// (the broker treats absent as <see cref="MessageDeliveryMode.Transient"/>).
        /// </summary>
        public MessageDeliveryMode? DeliveryMode { get; }

        /// <summary>
        /// AMQP <c>type</c> property — typically a logical event name set by the publisher
        /// independently of the .NET CLR type. <c>null</c> when not set on the wire.
        /// </summary>
        public string? Type { get; }

        /// <summary>
        /// AMQP <c>app-id</c> property — identifies the publishing application.
        /// <c>null</c> when not set on the wire.
        /// </summary>
        public string? AppId { get; }

        /// <summary>
        /// AMQP <c>expiration</c> (per-message TTL) decoded from its millisecond string representation.
        /// <c>null</c> when not set on the wire. The broker enforces the TTL; this value is informational.
        /// </summary>
        public TimeSpan? Expiration { get; }

        /// <summary>
        /// AMQP <c>priority</c> property (0–9). <c>null</c> when not set on the wire.
        /// Only meaningful on priority queues — declare the queue via
        /// <see cref="AutoGenerateSettings{TConsumer}.MaxPriority"/> for the broker to honor it.
        /// </summary>
        public byte? Priority { get; }

        /// <summary>
        /// AMQP <c>timestamp</c> property decoded from AMQP Unix seconds.
        /// <c>null</c> when not set on the wire.
        /// </summary>
        public DateTimeOffset? Timestamp { get; }

        /// <summary>
        /// AMQP <c>reply-to</c> property. Names the queue/exchange the publisher expects a reply on
        /// (common in RPC-style flows). <c>null</c> when not set on the wire.
        /// </summary>
        public string? ReplyTo { get; }

        /// <summary>
        /// AMQP <c>content-type</c> property. <c>null</c> when not set on the wire.
        /// </summary>
        public string? ContentType { get; }
    }
}
