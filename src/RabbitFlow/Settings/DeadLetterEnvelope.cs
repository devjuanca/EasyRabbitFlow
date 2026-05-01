using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyRabbitFlow.Settings
{
    /// <summary>
    /// Wrapper written to a dead-letter queue when <see cref="ConsumerSettings{TConsumer}.ExtendDeadletterMessage"/> is enabled.
    /// Carries the original message payload together with failure metadata.
    /// </summary>
    public sealed class DeadLetterEnvelope
    {
        /// <summary>UTC timestamp at which the failure was recorded.</summary>
        [JsonPropertyName("dateUtc")]
        public DateTime DateUtc { get; set; }

        /// <summary>The CLR type name of the original event.</summary>
        [JsonPropertyName("messageType")]
        public string? MessageType { get; set; }

        /// <summary>
        /// The <c>MessageId</c> of the original message, captured from <c>BasicProperties</c> when the failure was recorded.
        /// Preserved so the reprocessor can restore it when re-publishing to the main queue.
        /// </summary>
        [JsonPropertyName("messageId")]
        public string? MessageId { get; set; }

        /// <summary>
        /// The <c>CorrelationId</c> of the original message, captured from <c>BasicProperties</c> when the failure was recorded.
        /// Preserved so the reprocessor can restore it when re-publishing to the main queue.
        /// </summary>
        [JsonPropertyName("correlationId")]
        public string? CorrelationId { get; set; }

        /// <summary>
        /// The original message payload, stored as a raw JSON element so it can be re-published byte-equivalently
        /// regardless of whether the payload is an object, array, string, number, boolean or null.
        /// </summary>
        [JsonPropertyName("messageData")]
        public JsonElement? MessageData { get; set; }

        /// <summary>The CLR type name of the exception that caused the failure.</summary>
        [JsonPropertyName("exceptionType")]
        public string? ExceptionType { get; set; }

        /// <summary>The exception message.</summary>
        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }

        /// <summary>The exception stack trace.</summary>
        [JsonPropertyName("stackTrace")]
        public string? StackTrace { get; set; }

        /// <summary>The exception <c>Source</c> property.</summary>
        [JsonPropertyName("source")]
        public string? Source { get; set; }

        /// <summary>Inner exception chain captured at the time of failure.</summary>
        [JsonPropertyName("innerExceptions")]
        public List<DeadLetterInnerException> InnerExceptions { get; set; } = new List<DeadLetterInnerException>();

        /// <summary>
        /// Number of times this message has been re-queued from the dead-letter queue back to the main queue
        /// by the dead-letter reprocessor. <c>0</c> means the message has not been reprocessed yet.
        /// </summary>
        [JsonPropertyName("reprocessAttempts")]
        public int ReprocessAttempts { get; set; }
    }

    /// <summary>
    /// Inner exception entry inside a <see cref="DeadLetterEnvelope"/>.
    /// </summary>
    public sealed class DeadLetterInnerException
    {
        /// <summary>The CLR type name of the inner exception.</summary>
        [JsonPropertyName("exceptionType")]
        public string? ExceptionType { get; set; }

        /// <summary>The inner exception message.</summary>
        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }

        /// <summary>The inner exception stack trace.</summary>
        [JsonPropertyName("stackTrace")]
        public string? StackTrace { get; set; }

        /// <summary>The inner exception <c>Source</c> property.</summary>
        [JsonPropertyName("source")]
        public string? Source { get; set; }
    }
}
