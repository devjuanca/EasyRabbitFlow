using System;
using EasyRabbitFlow.Exceptions;

namespace EasyRabbitFlow.Settings
{
    internal static class RabbitFlowNameRules
    {
        private static readonly string[] ReservedSubstrings = new[]
        {
            "deadletter",
            "-exchange",
            "-routing-key"
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
