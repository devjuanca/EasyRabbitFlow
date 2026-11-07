using System;

namespace EasyRabbitFlow.Exceptions
{
    /// <summary>
    /// A mandatory publication was returned by the broker. Exposed through the publish result's Error;
    /// it does not imply that other messages in a batch were rolled back.
    /// </summary>
    public sealed class RabbitFlowUnroutableMessageException : RabbitFlowException
    {
        /// <summary>The exchange used for the publication (empty for direct-to-queue publishing).</summary>
        public string Exchange { get; }
        /// <summary>The routing key used for the publication.</summary>
        public string RoutingKey { get; }
        /// <summary>The broker's return code, typically 312 (NO_ROUTE).</summary>
        public ushort ReplyCode { get; }
        /// <summary>The broker's return explanation.</summary>
        public string ReplyText { get; }

        /// <summary>Creates an error describing a returned publication.</summary>
        public RabbitFlowUnroutableMessageException(string exchange, string routingKey,
            ushort replyCode, string replyText, Exception? innerException = null)
            : base($"Mandatory publication returned: {replyCode} {replyText}. Exchange='{exchange}', RoutingKey='{routingKey}'.", innerException!)
        {
            Exchange = exchange;
            RoutingKey = routingKey;
            ReplyCode = replyCode;
            ReplyText = replyText;
        }
    }
}
