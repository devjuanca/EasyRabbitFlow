using System;
using EasyRabbitFlow.Exceptions;

namespace EasyRabbitFlow.Settings
{
    internal static class RabbitFlowNameRules
    {
        // Derived from RabbitFlowTopologyNames so the reserved lists can never drift from the suffixes the
        // framework actually appends. "deadletter" is matched without its leading dash on purpose: that is
        // stricter and rejects any user name containing the substring, not only the exact "-deadletter" suffix.
        private static readonly string DeadLetterMarker = RabbitFlowTopologyNames.DeadLetterSuffix.TrimStart('-');

        // Queue names are the base every generated name is derived from, so all three suffixes are reserved.
        private static readonly string[] QueueNameReserved = new[]
        {
            DeadLetterMarker,
            RabbitFlowTopologyNames.ExchangeSuffix,
            RabbitFlowTopologyNames.RoutingKeySuffix
        };

        // Exchange names and routing keys can only collide with the generated dead-letter names, which always
        // contain "deadletter" because they derive from the queue name. "-exchange"/"-routing-key" are natural
        // parts of these names (e.g. binding to an existing "snapshots-exchange") and stay allowed, matching
        // the rule applied by RabbitFlowConfigurator.DeclareExchange.
        private static readonly string[] BindingNameReserved = new[]
        {
            DeadLetterMarker
        };

        /// <summary>Validates a consumer queue name: rejects <c>deadletter</c>, <c>-exchange</c> and <c>-routing-key</c>.</summary>
        public static void ValidateQueueName(string? name, string paramName) => Validate(name, paramName, QueueNameReserved);

        /// <summary>Validates an auto-generate exchange name or routing key: rejects only <c>deadletter</c>.</summary>
        public static void ValidateBindingName(string? name, string paramName) => Validate(name, paramName, BindingNameReserved);

        private static void Validate(string? name, string paramName, string[] reservedSubstrings)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            foreach (var reserved in reservedSubstrings)
            {
                if (name!.IndexOf(reserved, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw new RabbitFlowException(
                        $"{paramName} '{name}' contains the reserved substring '{reserved}'. " +
                        $"The framework appends these substrings when auto-generating topology, so they cannot appear in this name. Reserved for {paramName}: {string.Join(", ", reservedSubstrings)}.");
                }
            }
        }
    }
}
