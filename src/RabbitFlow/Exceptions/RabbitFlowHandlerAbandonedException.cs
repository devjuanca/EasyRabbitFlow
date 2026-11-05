using System;

namespace EasyRabbitFlow.Exceptions
{
    /// <summary>
    /// Raised by the consumer when a handler did not complete within <c>ConsumerSettings.Timeout</c> plus the
    /// 30-second abandon grace and did not honor its cancellation token. The wait on it was abandoned to free the
    /// prefetch slot; the handler task keeps running detached. Treated as a permanent failure (a handler that
    /// ignores cancellation is a bug to fix, not a condition to retry): the message is dead-lettered and parked.
    /// </summary>
    public sealed class RabbitFlowHandlerAbandonedException : RabbitFlowException
    {
        public TimeSpan Timeout { get; }

        public TimeSpan Grace { get; }

        public RabbitFlowHandlerAbandonedException(string consumerName, TimeSpan timeout, TimeSpan grace)
            : base($"Handler {consumerName} did not complete within its Timeout ({timeout}) plus the abandon grace period ({grace}) and did not honor cancellation. " +
                   "The wait was abandoned to free the prefetch slot; the still-running handler task was left detached.")
        {
            Timeout = timeout;
            Grace = grace;
        }
    }
}
