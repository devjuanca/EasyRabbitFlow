namespace EasyRabbitFlow.Settings
{
    // Decides whether a delivery is given up on without invoking the handler because the broker has already
    // returned it MaxBrokerDeliveries times. Kept pure so the boundaries can be unit-tested without a broker.
    internal static class ConsumerPoisonGuard
    {
        /// <summary>
        /// True when the delivery must be dead-lettered instead of handled.
        /// </summary>
        /// <param name="maxBrokerDeliveries">Configured limit; <c>null</c> or non-positive disables the guard.</param>
        /// <param name="deliveryCount">Broker's <c>x-delivery-count</c>; <c>null</c> when absent (first delivery, classic queue).</param>
        /// <param name="redelivered">The broker's redelivered flag. A count on a first delivery is publisher-supplied
        /// or a leftover of a manual replay, not evidence of a loop, so it is ignored.</param>
        /// <param name="autoAckOnError">With AutoAckOnError the message would be acknowledged unprocessed, which is
        /// silent loss; the guard then stays out of the way and the handler runs.</param>
        /// <param name="hasDeadLetterQueue">Whether the auto-generated dead-letter queue exists for this consumer. Without
        /// it a rejected delivery is dropped unless the queue has its own dead-letter exchange, so the guard is skipped.</param>
        internal static bool ShouldGiveUp(int? maxBrokerDeliveries, int? deliveryCount, bool redelivered, bool autoAckOnError, bool hasDeadLetterQueue)
        {
            if (maxBrokerDeliveries == null || maxBrokerDeliveries.Value <= 0)
            {
                return false;
            }

            if (deliveryCount == null || !redelivered)
            {
                return false;
            }

            if (autoAckOnError || !hasDeadLetterQueue)
            {
                return false;
            }

            return deliveryCount.Value >= maxBrokerDeliveries.Value;
        }
    }
}
