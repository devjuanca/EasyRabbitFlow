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
