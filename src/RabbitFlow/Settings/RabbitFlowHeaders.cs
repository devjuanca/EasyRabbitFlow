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
        /// Header set by the broker on redelivered messages from <b>quorum</b> queues: how many times the delivery
        /// was returned to the queue (consumer timeout, cancelled consumer, closed channel). Classic queues never
        /// set it; they only flag <c>redelivered</c>.
        /// </summary>
        public const string DeliveryCount = "x-delivery-count";

        /// <summary>
        /// Headers the broker writes about a delivery's history (<c>x-delivery-count</c>, <c>x-death</c>,
        /// <c>x-first-death-*</c>, <c>x-last-death-*</c>). They describe the original delivery, not a copy the
        /// library re-publishes, so they are left out of the dead-letter envelope.
        /// </summary>
        public static bool IsBrokerOwned(string header)
        {
            return header == DeliveryCount
                || header == "x-death"
                || header.StartsWith("x-first-death-", System.StringComparison.Ordinal)
                || header.StartsWith("x-last-death-", System.StringComparison.Ordinal);
        }

        /// <summary>
        /// Reads the broker's <see cref="DeliveryCount"/> header. <c>null</c> when absent (first delivery, or a
        /// classic queue) or unparseable.
        /// </summary>
        public static int? ReadDeliveryCount(IDictionary<string, object?>? headers)
        {
            if (headers == null)
                return null;

            if (!headers.TryGetValue(DeliveryCount, out var raw) || raw == null)
                return null;

            return raw switch
            {
                int i => i,
                long l => l > int.MaxValue ? int.MaxValue : (int)l,
                short s => s,
                byte b => b,
                byte[] bytes => int.TryParse(Encoding.UTF8.GetString(bytes), out var parsedBytes) ? parsedBytes : (int?)null,
                string str => int.TryParse(str, out var parsedStr) ? parsedStr : (int?)null,
                _ => null,
            };
        }

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
