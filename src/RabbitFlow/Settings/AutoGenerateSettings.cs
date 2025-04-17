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
        /// Gets or sets a value indicating whether to generate an exchange.
        /// </summary>
        public bool GenerateExchange { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating wether to generate and bind a deadletter queue.
        /// </summary>
        public bool GenerateDeadletterQueue { get; set; } = true;

        /// <summary>
        /// Gets or sets the type of exchange to be generated.
        /// </summary>
        public ExchangeType ExchangeType { get; set; } = ExchangeType.Direct;

        /// <summary>
        /// Gets or sets the name of the exchange to be generated.
        /// </summary>
        public string? ExchangeName { get; set; } = null;

        /// <summary>
        /// Gets or sets the routing key for the exchange.
        /// </summary>
        public string? RoutingKey { get; set; } = null;

        /// <summary>
        /// Gets or sets a value indicating whether the exchange should be durable.
        /// </summary>
        public bool DurableExchange { get; set; } = true;

        /// <summary>
        /// Should this queue will survive a broker restart?
        /// </summary>
        public bool DurableQueue { get; set; } = true;

        /// <summary>
        /// Should this queue use be limited to its declaring connection? Such a queue will be deleted when its declaring connection closes.
        /// </summary>
        public bool ExclusiveQueue { get; set; } = false;

        /// <summary>
        /// Should this queue be auto-deleted when its last consumer (if any) unsubscribes?
        /// </summary>
        public bool AutoDeleteQueue { get; set; } = false;

        /// <summary>
        /// Gets or sets additional arguments for the queue or exchange.
        /// </summary>
        public IDictionary<string, object?>? Args { get; set; } = null;
    }

    /// <summary>
    /// Enum representing the type of exchange.
    /// </summary>
    public enum ExchangeType
    {
        /// <summary>
        /// Direct exchange type.
        /// </summary>
        Direct,

        /// <summary>
        /// Fanout exchange type.
        /// </summary>
        Fanout
    }

}
