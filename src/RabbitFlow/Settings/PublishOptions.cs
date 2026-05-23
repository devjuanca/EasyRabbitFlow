using System;
using System.Collections.Generic;
using System.Text.Json;

namespace EasyRabbitFlow.Settings
{
    /// <summary>
    /// Optional AMQP metadata applied to messages published through <see cref="Services.IRabbitFlowPublisher"/>.
    /// All typed fields except <see cref="DeliveryMode"/> are <c>null</c> by default — when left unset they are
    /// not written to the message. Use <see cref="Headers"/> as an escape hatch for any custom header
    /// or AMQP property not yet exposed as a typed field.
    /// </summary>
    /// <remarks>
    /// <see cref="Services.IRabbitFlowPublisher"/> intentionally does not expose <c>MessageId</c> or <c>CorrelationId</c>
    /// here. Those have dedicated parameters on each publish method.
    /// </remarks>
    public sealed class PublishOptions
    {
        /// <summary>
        /// AMQP <c>type</c> property. Often used to carry a logical event name independent
        /// from the .NET CLR type (e.g. <c>"OrderCreated"</c>).
        /// </summary>
        public string? Type { get; set; }

        /// <summary>
        /// AMQP <c>app-id</c> property. Identifies the publishing application (e.g. <c>"checkout-svc"</c>).
        /// </summary>
        public string? AppId { get; set; }

        /// <summary>
        /// AMQP <c>expiration</c> (per-message TTL). When set, the broker discards (or dead-letters)
        /// the message if it is not consumed within this window. Converted internally to a string
        /// of milliseconds as required by AMQP.
        /// </summary>
        public TimeSpan? Expiration { get; set; }

        /// <summary>
        /// AMQP <c>priority</c> property (0–9). Only effective on priority queues.
        /// </summary>
        public byte? Priority { get; set; }

        /// <summary>
        /// AMQP <c>timestamp</c> property. Converted internally to AMQP Unix seconds.
        /// </summary>
        public DateTimeOffset? Timestamp { get; set; }

        /// <summary>
        /// AMQP <c>reply-to</c> property. Names a queue/exchange the consumer should reply to
        /// (common in RPC-style flows).
        /// </summary>
        public string? ReplyTo { get; set; }

        /// <summary>
        /// AMQP <c>content-type</c> property. When <c>null</c> (default) it is not set on the wire,
        /// preserving the library's historical behavior. Set to <c>"application/json"</c> (matching the
        /// serialized payload) or any custom value if your consumers need it.
        /// </summary>
        public string? ContentType { get; set; }

        /// <summary>
        /// Delivery mode for the message. Defaults to <see cref="MessageDeliveryMode.Transient"/> to
        /// preserve the library's historical behavior. Set to <see cref="MessageDeliveryMode.Persistent"/>
        /// when you need messages to survive a broker restart (the destination queue must also be durable).
        /// </summary>
        public MessageDeliveryMode DeliveryMode { get; set; } = MessageDeliveryMode.Transient;

        /// <summary>
        /// Free-form AMQP headers attached to the message (the AMQP <c>headers</c> table).
        /// Use for business metadata, routing values for headers exchanges, or any field not yet
        /// exposed as a typed property on <see cref="PublishOptions"/>.
        /// </summary>
        public IDictionary<string, object?>? Headers { get; set; }

        /// <summary>
        /// Per-call override for the JSON serializer used to encode the message body.
        /// When <c>null</c> (default), the publisher uses the <see cref="JsonSerializerOptions"/>
        /// registered globally via <c>SetCustomJsonSerializerOptions</c> (or <see cref="JsonSerializerOptions.Web"/>
        /// if none was configured).
        /// </summary>
        public JsonSerializerOptions? JsonOptions { get; set; }
    }

    /// <summary>
    /// Delivery mode for a published AMQP message.
    /// </summary>
    public enum MessageDeliveryMode : byte
    {
        /// <summary>
        /// Non-persistent. The broker may keep the message in memory only — it is lost on broker restart,
        /// even when the destination queue is durable.
        /// </summary>
        Transient = 1,

        /// <summary>
        /// Persistent. The broker writes the message to disk so it survives a restart
        /// (only effective when the destination queue is also durable).
        /// </summary>
        Persistent = 2
    }
}
