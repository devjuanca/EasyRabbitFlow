using System.Collections.Generic;

namespace EasyRabbitFlow.Settings
{
    /// <summary>
    /// Class to generate settings for auto-generation of queues and exchanges for a consumer.
    /// </summary>
    /// <typeparam name="TConsumer">Type of the consumer.</typeparam>
    public class AutoGenerateSettings<TConsumer> where TConsumer : class
    {
        /// <summary>
        /// Gets or sets a value indicating whether to generate an exchange. Default is true.
        /// </summary>
        public bool GenerateExchange { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating wether to generate and bind a deadletter queue. Default is true.
        /// </summary>
        public bool GenerateDeadletterQueue { get; set; } = true;

        /// <summary>
        /// Gets or sets the type of exchange to be generated. Default is <see cref="ExchangeType.Direct"/>.
        /// </summary>
        public ExchangeType ExchangeType { get; set; } = ExchangeType.Direct;

        /// <summary>
        /// Gets or sets the name of the exchange to be generated.
        /// </summary>
        public string? ExchangeName { get; set; }

        /// <summary>
        /// Gets or sets the routing key for the exchange.
        /// </summary>
        public string? RoutingKey { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the exchange should be durable. Default is true.
        /// </summary>
        public bool DurableExchange { get; set; } = true;

        /// <summary>
        /// Should this queue will survive a broker restart? Default is true.
        /// </summary>
        public bool DurableQueue { get; set; } = true;

        /// <summary>
        /// Should this queue use be limited to its declaring connection? Such a queue will be deleted when its declaring connection closes. Default is false.
        /// </summary>
        public bool ExclusiveQueue { get; set; } = false;

        /// <summary>
        /// Should this queue be auto-deleted when its last consumer (if any) unsubscribes? Default is false.
        /// </summary>
        public bool AutoDeleteQueue { get; set; } = false;

        /// <summary>
        /// Gets or sets additional arguments for the queue or exchange. Default is null.
        /// </summary>
        public IDictionary<string, object?>? Args { get; set; } = null;

        /// <summary>
        /// When set, declares the auto-generated queue as a priority queue with the given maximum priority,
        /// emitting the <c>x-max-priority</c> queue argument. Required for the broker to honor the
        /// <c>Priority</c> field set via <see cref="PublishOptions.Priority"/> — messages published with a
        /// priority to a non-priority queue are delivered in FIFO order regardless.
        /// <para>
        /// RabbitMQ recommends a small value (1–10) since each priority level allocates internal resources.
        /// If <see cref="Args"/> already contains <c>x-max-priority</c>, that explicit entry wins and this
        /// property is ignored.
        /// </para>
        /// <para>
        /// Default is <c>null</c> (the queue is declared without <c>x-max-priority</c>).
        /// </para>
        /// </summary>
        public byte? MaxPriority { get; set; } = null;

        /// <summary>
        /// Extra queues that should be bound to the auto-generated dead-letter exchange in addition to
        /// the primary dead-letter queue. Every entry is declared at startup and bound to
        /// <c>{queueName}-deadletter-exchange</c> with routing key <c>{queueName}-deadletter-routing-key</c>,
        /// so each dead-lettered message is delivered as a copy to every queue in this list.
        /// <para>
        /// Useful for audit/observability copies, alerting consumers, or any side-channel processing
        /// that must not interfere with the primary dead-letter queue or the reprocessor. Requires
        /// <see cref="GenerateDeadletterQueue"/> to be <c>true</c>; otherwise the list is ignored
        /// and a warning is logged at startup.
        /// </para>
        /// </summary>
        public List<DeadLetterReplica> DeadLetterReplicas { get; set; } = new List<DeadLetterReplica>();
    }

    /// <summary>
    /// Declares an additional queue that should receive a copy of every message routed to the
    /// auto-generated dead-letter exchange of a consumer. Use it to replicate dead-lettered messages
    /// into audit, alerting, or side-channel processing consumers without affecting the primary
    /// dead-letter queue or the reprocessor.
    /// </summary>
    public class DeadLetterReplica
    {
        /// <summary>
        /// Name of the queue to declare and bind to the dead-letter exchange. Must be non-empty.
        /// </summary>
        public string QueueName { get; set; } = string.Empty;

        /// <summary>
        /// Whether the queue survives broker restarts. Default is true.
        /// </summary>
        public bool Durable { get; set; } = true;

        /// <summary>
        /// Whether the queue is auto-deleted when its last consumer unsubscribes. Default is false.
        /// </summary>
        public bool AutoDelete { get; set; } = false;

        /// <summary>
        /// Optional queue arguments (e.g. <c>x-message-ttl</c>, <c>x-max-length</c>). Default is null.
        /// </summary>
        public IDictionary<string, object?>? Arguments { get; set; } = null;
    }

    /// <summary>
    /// RabbitMQ exchange type. Determines how the broker routes a published message to bound queues.
    /// </summary>
    public enum ExchangeType
    {
        /// <summary>
        /// Routes a message to every queue whose binding key is an exact match for the message's routing key.
        /// <para>
        /// Best for point-to-point delivery and simple work queues where producers know which logical destination
        /// to address (e.g. routing key <c>"orders.created"</c> reaches only queues bound with exactly that key).
        /// This is the default and the most common choice for command/event-per-queue topologies.
        /// </para>
        /// </summary>
        Direct,

        /// <summary>
        /// Broadcasts every message to all queues bound to the exchange, ignoring the routing key entirely.
        /// <para>
        /// Best for pub/sub fan-out: one publish reaches every subscriber. Typical use cases include
        /// notifications that need to land in multiple independent consumers (e.g. send the same
        /// "user-signed-up" event to email, analytics, and audit consumers, each with its own queue).
        /// </para>
        /// </summary>
        Fanout,

        /// <summary>
        /// Routes a message to queues whose binding key matches the message's routing key using a dotted
        /// pattern with <c>*</c> (exactly one word) and <c>#</c> (zero or more words) wildcards.
        /// <para>
        /// Best when subscribers want to filter by a hierarchical key — e.g. routing key
        /// <c>"orders.eu.created"</c> can reach a queue bound to <c>"orders.*.created"</c>
        /// (per-region) and another bound to <c>"orders.#"</c> (everything orders-related). Use it
        /// to express category/region/severity-style routing without forcing the publisher to know
        /// every subscriber.
        /// </para>
        /// </summary>
        Topic,

        /// <summary>
        /// Routes based on message <em>headers</em> instead of the routing key. Bindings declare a header set
        /// and an <c>x-match</c> argument (<c>all</c> = AND, <c>any</c> = OR) that the broker evaluates against
        /// the message's headers.
        /// <para>
        /// Best when routing depends on multiple, non-hierarchical attributes (e.g. <c>format=pdf</c> AND
        /// <c>tier=premium</c>) that don't fit naturally into a single dotted routing key. More expressive than
        /// Topic but slower, and bindings must be configured outside this library.
        /// </para>
        /// </summary>
        Headers
    }

}
