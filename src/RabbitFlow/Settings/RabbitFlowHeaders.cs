using System.Collections.Generic;
using System.Text;

namespace EasyRabbitFlow.Settings
{
    /// <summary>
    /// Internal AMQP header names and helpers used by EasyRabbitFlow.
    /// </summary>
    internal static class RabbitFlowHeaders
    {
        /// <summary>
        /// Header carrying the current reprocess-attempt counter as a message travels from the
        /// dead-letter queue back to the main queue and (if it fails again) into the dead-letter envelope.
        /// </summary>
        public const string ReprocessAttempts = "x-reprocess-attempts";

        /// <summary>
        /// Reads the <see cref="ReprocessAttempts"/> AMQP header in a defensive way (RabbitMQ.Client
        /// may surface header values as <c>int</c>, <c>long</c>, <c>byte[]</c>, <c>string</c>, etc.).
        /// Returns <c>0</c> when the header is missing or unparseable.
        /// </summary>
        public static int ReadReprocessAttempts(IDictionary<string, object?>? headers)
        {
            if (headers == null) 
                return 0;

            if (!headers.TryGetValue(ReprocessAttempts, out var raw) || raw == null) 
                return 0;

            return raw switch
            {
                int i => i,
                long l => (int)l,
                short s => s,
                byte b => b,
                byte[] bytes => int.TryParse(Encoding.UTF8.GetString(bytes), out var parsedBytes) ? parsedBytes : 0,
                string str => int.TryParse(str, out var parsedStr) ? parsedStr : 0,
                _ => 0,
            };
        }
    }
}
