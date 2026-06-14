namespace EasyRabbitFlow.Settings
{
    /// <summary>
    /// Single source of truth for the names EasyRabbitFlow derives from a consumer's queue name when it
    /// auto-generates topology (exchange, routing key, dead-letter queue/exchange/routing-key, parking queue).
    /// <para>
    /// The consumer (which declares and writes the dead-letter topology) and the dead-letter reprocessor
    /// (which reads it back) must agree on these names byte-for-byte; centralizing the convention here keeps
    /// them from drifting apart silently. <see cref="RabbitFlowNameRules"/> reserves the same suffixes so
    /// user-supplied names can never collide with the generated ones.
    /// </para>
    /// </summary>
    internal static class RabbitFlowTopologyNames
    {
        public const string DeadLetterSuffix = "-deadletter";

        public const string ExchangeSuffix = "-exchange";

        public const string RoutingKeySuffix = "-routing-key";

        public const string ParkingSuffix = "-parking";

        /// <summary>Default exchange name for the main queue: <c>{queue}-exchange</c>.</summary>
        public static string Exchange(string queue) => queue + ExchangeSuffix;

        /// <summary>Default routing key for the main queue: <c>{queue}-routing-key</c>.</summary>
        public static string RoutingKey(string queue) => queue + RoutingKeySuffix;

        /// <summary>Dead-letter queue: <c>{queue}-deadletter</c>.</summary>
        public static string DeadLetterQueue(string queue) => queue + DeadLetterSuffix;

        /// <summary>Dead-letter exchange: <c>{queue}-deadletter-exchange</c>.</summary>
        public static string DeadLetterExchange(string queue) => queue + DeadLetterSuffix + ExchangeSuffix;

        /// <summary>Dead-letter routing key: <c>{queue}-deadletter-routing-key</c>.</summary>
        public static string DeadLetterRoutingKey(string queue) => queue + DeadLetterSuffix + RoutingKeySuffix;

        /// <summary>Parking queue for non-reprocessable messages: <c>{queue}-deadletter-parking</c>.</summary>
        public static string ParkingQueue(string queue) => queue + DeadLetterSuffix + ParkingSuffix;
    }
}
