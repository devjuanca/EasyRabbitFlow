using System.Collections.Generic;

namespace EasyRabbitFlow.Settings
{
    /// <summary>
    /// A single application-owned exchange declaration, created at startup independently of any consumer.
    /// Registered via <c>DeclareExchange</c> on the RabbitFlow configurator; external clients are expected
    /// to declare and bind their own queues to it.
    /// </summary>
    public class ExchangeDeclaration
    {
        internal ExchangeDeclaration(string exchangeName, ExchangeType exchangeType)
        {
            ExchangeName = exchangeName;
            ExchangeType = exchangeType;
        }

        /// <summary>
        /// Name of the exchange.
        /// </summary>
        public string ExchangeName { get; }

        /// <summary>
        /// Routing semantics of the exchange.
        /// </summary>
        public ExchangeType ExchangeType { get; }

        /// <summary>
        /// Whether the exchange survives broker restarts. Default is true.
        /// </summary>
        public bool Durable { get; set; } = true;

        /// <summary>
        /// Whether the exchange is deleted when its last bound queue is unbound. Default is false.
        /// Rarely what a publisher-owned exchange wants: subscribers come and go independently.
        /// </summary>
        public bool AutoDelete { get; set; } = false;

        /// <summary>
        /// Optional exchange arguments (e.g. <c>alternate-exchange</c>). Default is null.
        /// </summary>
        public IDictionary<string, object?>? Args { get; set; } = null;
    }
}
