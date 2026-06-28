namespace EasyRabbitFlow.Settings
{
    /// <summary>
    /// Controls what the dead-letter reprocessor does with a message that has reached a terminal,
    /// non-reprocessable state: either an <b>exhausted</b> message (it ran out of reprocess attempts)
    /// or a <b>permanent</b> failure (its exception was classified as non-transient).
    /// </summary>
    /// <remarks>
    /// This setting does <b>not</b> govern <i>malformed</i> messages (an envelope that cannot be deserialized
    /// or carries no payload). Those are always moved to the parking queue regardless of this value, because
    /// discarding bytes that could not be parsed would drop data with no recoverable trace.
    /// </remarks>
    public enum DeadLetterFinalAction
    {
        /// <summary>
        /// Move the message to the parking queue (<c>{queue}-deadletter-parking</c>) so it remains visible
        /// from a RabbitMQ client and is never silently dropped. This is the default and preserves the
        /// historical behavior.
        /// </summary>
        Park = 0,

        /// <summary>
        /// Acknowledge the message off the dead-letter queue without re-publishing it anywhere — the message
        /// is permanently discarded. The parking queue is never created on account of exhausted/permanent
        /// messages in this mode. Use only when these failures carry no value and you have another trail
        /// (e.g. a registered parked-message handler) for whatever you still need to observe.
        /// </summary>
        Discard = 1
    }
}
