using System;

namespace EasyRabbitFlow.Exceptions
{
    /// <summary>
    /// Raised by the consumer, instead of invoking the handler, when the broker has already returned the same
    /// delivery <see cref="DeliveryCount"/> times (quorum queue header <c>x-delivery-count</c>) and that count
    /// reached <c>ConsumerSettings.MaxBrokerDeliveries</c>. A message the broker keeps taking back (consumer
    /// timeouts, cancelled consumers, dropped channels) goes back to the head of its queue every time, so left
    /// alone it blocks the queue until the broker's own delivery limit drops it. Treated as a permanent failure:
    /// the message is dead-lettered with its envelope and the reprocessor parks it for inspection.
    /// </summary>
    public sealed class RabbitFlowPoisonMessageException : RabbitFlowException
    {
        /// <summary>
        /// Number of times the broker had returned the delivery when it was given up on.
        /// </summary>
        public int DeliveryCount { get; }

        /// <summary>
        /// The configured limit that was reached.
        /// </summary>
        public int MaxBrokerDeliveries { get; }

        public RabbitFlowPoisonMessageException(int deliveryCount, int maxBrokerDeliveries)
            : base($"The broker returned this delivery {deliveryCount} time(s) (x-delivery-count), reaching MaxBrokerDeliveries = {maxBrokerDeliveries}. " +
                   "It was dead-lettered without invoking the handler so it stops blocking the queue.")
        {
            DeliveryCount = deliveryCount;
            MaxBrokerDeliveries = maxBrokerDeliveries;
        }
    }
}
