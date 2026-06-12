using System;
using System.Collections.Generic;

namespace EasyRabbitFlow.Settings
{
    /// <summary>
    /// Describes the outcome of a temporary queue run.
    /// </summary>
    public class TemporaryRunResult
    {
        internal TemporaryRunResult(
            int totalMessages,
            int publishedMessages,
            int processedMessages,
            int succeededMessages,
            int failedMessages,
            string? correlationId,
            string? queueName,
            DateTime startedUtc,
            DateTime completedUtc,
            IReadOnlyList<TemporaryRunError> errors)
        {
            TotalMessages = totalMessages;
            PublishedMessages = publishedMessages;
            ProcessedMessages = processedMessages;
            SucceededMessages = succeededMessages;
            FailedMessages = failedMessages;
            CorrelationId = correlationId;
            QueueName = queueName;
            StartedUtc = startedUtc;
            CompletedUtc = completedUtc;
            Errors = errors;
        }

        /// <summary>
        /// Number of messages supplied by the caller.
        /// </summary>
        public int TotalMessages { get; }

        /// <summary>
        /// Number of messages successfully published to the temporary queue.
        /// </summary>
        public int PublishedMessages { get; }

        /// <summary>
        /// Number of messages whose handler completed, either successfully or with an error.
        /// </summary>
        public int ProcessedMessages { get; }

        /// <summary>
        /// Number of messages whose handler completed successfully.
        /// </summary>
        public int SucceededMessages { get; }

        /// <summary>
        /// Number of messages that failed to publish, deserialize, or process.
        /// </summary>
        public int FailedMessages { get; }

        /// <summary>
        /// Optional correlation id used for this temporary run.
        /// </summary>
        public string? CorrelationId { get; }

        /// <summary>
        /// Name of the generated temporary queue.
        /// </summary>
        public string? QueueName { get; }

        /// <summary>
        /// UTC timestamp when the run started.
        /// </summary>
        public DateTime StartedUtc { get; }

        /// <summary>
        /// UTC timestamp when the run completed.
        /// </summary>
        public DateTime CompletedUtc { get; }

        /// <summary>
        /// Total elapsed time for the run.
        /// </summary>
        public TimeSpan Duration => CompletedUtc - StartedUtc;

        /// <summary>
        /// True when every supplied message was published and processed successfully.
        /// </summary>
        public bool Success => TotalMessages == PublishedMessages
            && TotalMessages == ProcessedMessages
            && FailedMessages == 0;

        /// <summary>
        /// Errors observed while publishing or processing messages.
        /// </summary>
        public IReadOnlyList<TemporaryRunError> Errors { get; }

        internal static TemporaryRunResult Empty(string? correlationId, DateTime startedUtc)
            => new TemporaryRunResult(0, 0, 0, 0, 0, correlationId, null, startedUtc, DateTime.UtcNow, Array.Empty<TemporaryRunError>());
    }

    /// <summary>
    /// Describes the outcome of a temporary queue run that collected per-message results.
    /// </summary>
    /// <typeparam name="TResult">The type returned by each successful message handler.</typeparam>
    public sealed class TemporaryRunResult<TResult> : TemporaryRunResult
    {
        internal TemporaryRunResult(
            int totalMessages,
            int publishedMessages,
            int processedMessages,
            int succeededMessages,
            int failedMessages,
            string? correlationId,
            string? queueName,
            DateTime startedUtc,
            DateTime completedUtc,
            IReadOnlyList<TemporaryRunError> errors,
            IReadOnlyList<TResult> results)
            : base(totalMessages, publishedMessages, processedMessages, succeededMessages, failedMessages, correlationId, queueName, startedUtc, completedUtc, errors)
        {
            Results = results;
        }

        /// <summary>
        /// Results returned by successful message handlers.
        /// </summary>
        public IReadOnlyList<TResult> Results { get; }

        internal static new TemporaryRunResult<TResult> Empty(string? correlationId, DateTime startedUtc)
            => new TemporaryRunResult<TResult>(0, 0, 0, 0, 0, correlationId, null, startedUtc, DateTime.UtcNow, Array.Empty<TemporaryRunError>(), Array.Empty<TResult>());
    }

    /// <summary>
    /// Captures an error observed during a temporary queue run.
    /// </summary>
    public sealed class TemporaryRunError
    {
        internal TemporaryRunError(
            TemporaryRunErrorStage stage,
            string message,
            string? exceptionType = null,
            string? queueName = null,
            ulong? deliveryTag = null,
            int? messageIndex = null)
        {
            Stage = stage;
            Message = message;
            ExceptionType = exceptionType;
            QueueName = queueName;
            DeliveryTag = deliveryTag;
            MessageIndex = messageIndex;
        }

        /// <summary>
        /// Run stage where the error occurred.
        /// </summary>
        public TemporaryRunErrorStage Stage { get; }

        /// <summary>
        /// Human-readable error message.
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// Exception type name, when the error came from an exception.
        /// </summary>
        public string? ExceptionType { get; }

        /// <summary>
        /// Temporary queue name associated with the error, when available.
        /// </summary>
        public string? QueueName { get; }

        /// <summary>
        /// RabbitMQ delivery tag associated with the error, when available.
        /// </summary>
        public ulong? DeliveryTag { get; }

        /// <summary>
        /// Zero-based index of the failed message in the input collection.
        /// Populated for <see cref="TemporaryRunErrorStage.Publish"/> errors, where the message never reached the queue.
        /// </summary>
        public int? MessageIndex { get; }

        internal static TemporaryRunError FromException(TemporaryRunErrorStage stage, Exception exception, string? queueName = null, ulong? deliveryTag = null, int? messageIndex = null)
            => new TemporaryRunError(stage, exception.Message, exception.GetType().Name, queueName, deliveryTag, messageIndex);

        internal static TemporaryRunError FromMessage(TemporaryRunErrorStage stage, string message, string? queueName = null, ulong? deliveryTag = null, int? messageIndex = null)
            => new TemporaryRunError(stage, message, null, queueName, deliveryTag, messageIndex);
    }

    /// <summary>
    /// Stage of a temporary queue run where an error occurred.
    /// </summary>
    public enum TemporaryRunErrorStage
    {
        Publish,
        Deserialize,
        Process,
        Timeout,
        Cancellation,
        Completion,
        ConnectionLost
    }
}
