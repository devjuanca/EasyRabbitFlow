using System;
using EasyRabbitFlow.Exceptions;

namespace EasyRabbitFlow.Settings
{
    internal static class RabbitFlowNameRules
    {
        // Derived from RabbitFlowTopologyNames so the reserved list can never drift from the suffixes the
        // framework actually appends. "deadletter" is matched without its leading dash on purpose: that is
        // stricter and rejects any user name containing the substring, not only the exact "-deadletter" suffix.
        private static readonly string[] ReservedSubstrings = new[]
        {
            RabbitFlowTopologyNames.DeadLetterSuffix.TrimStart('-'),
            RabbitFlowTopologyNames.ExchangeSuffix,
            RabbitFlowTopologyNames.RoutingKeySuffix
        };

        public static void Validate(string? name, string paramName)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            foreach (var reserved in ReservedSubstrings)
            {
                if (name!.IndexOf(reserved, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw new RabbitFlowException(
                        $"{paramName} '{name}' contains the reserved substring '{reserved}'. " +
                        $"The framework appends these substrings when auto-generating topology, so they cannot appear in user-supplied names. Reserved: {string.Join(", ", ReservedSubstrings)}.");
                }
            }
        }
    }
}
