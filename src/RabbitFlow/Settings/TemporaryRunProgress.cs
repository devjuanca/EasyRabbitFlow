using System;

namespace EasyRabbitFlow.Settings
{
    /// <summary>
    /// Point-in-time snapshot of a temporary run in progress, passed to <see cref="RunTemporaryOptions.OnProgress"/>.
    /// Counters only grow during a run; the final figures are delivered by the completion callback as a
    /// <see cref="TemporaryRunResult"/>.
    /// </summary>
    public sealed class TemporaryRunProgress
    {
        internal TemporaryRunProgress(
            string? correlationId,
            string queueName,
            DateTime startedUtc,
            DateTime timestampUtc,
            int totalMessages,
            int publishedMessages,
            int processedMessages,
            int succeededMessages,
            int failedMessages,
            int inFlightMessages)
        {
            CorrelationId = correlationId;
            QueueName = queueName;
            StartedUtc = startedUtc;
            TimestampUtc = timestampUtc;
            TotalMessages = totalMessages;
            PublishedMessages = publishedMessages;
            ProcessedMessages = processedMessages;
            SucceededMessages = succeededMessages;
            FailedMessages = failedMessages;
            InFlightMessages = inFlightMessages;
        }

        /// <summary>Correlation identifier of the run (<see cref="RunTemporaryOptions.CorrelationId"/>).</summary>
        public string? CorrelationId { get; }

        /// <summary>Name of the temporary queue used by the run.</summary>
        public string QueueName { get; }

        /// <summary>UTC time when the run started.</summary>
        public DateTime StartedUtc { get; }

        /// <summary>
        /// UTC time this snapshot was taken. Snapshots are emitted on every <see cref="RunTemporaryOptions.ProgressInterval"/>
        /// even when nothing changed, so a timestamp that stops advancing means the run (or its process) is gone.
        /// </summary>
        public DateTime TimestampUtc { get; }

        /// <summary>
        /// Messages supplied so far. For collections this is the full count from the start; for asynchronous
        /// sources it grows as elements are pulled from the source.
        /// </summary>
        public int TotalMessages { get; }

        /// <summary>Messages published to the temporary queue so far.</summary>
        public int PublishedMessages { get; }

        /// <summary>Messages whose handler has finished, successfully or not.</summary>
        public int ProcessedMessages { get; }

        /// <summary>Messages processed successfully so far.</summary>
        public int SucceededMessages { get; }

        /// <summary>Messages that failed so far (publish, deserialization, handler error or timeout).</summary>
        public int FailedMessages { get; }

        /// <summary>Handlers running at the moment of the snapshot.</summary>
        public int InFlightMessages { get; }
    }
}
