using System;
using System.Threading;
using System.Threading.Tasks;

namespace EasyRabbitFlow.Settings
{
    /// <summary>
    /// Configuration options for executing a temporary RabbitMQ message flow.
    /// </summary>
    public class RunTemporaryOptions
    {
        /// <summary>
        /// A custom correlation ID used for logging and tracing the execution flow.
        /// </summary>
        public string? CorrelationId { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// The number of unacknowledged messages that can be pre-fetched by the consumer at a time.
        /// Default is 1.
        /// </summary>
        public ushort PrefetchCount
        {
            get => _prefetchCount;
            set => _prefetchCount = value == 0 ? throw new ArgumentOutOfRangeException(nameof(PrefetchCount), "PrefetchCount must be greater than 0.") : value;
        }

        private ushort _prefetchCount = 1;

        /// <summary>
        /// Optional timeout duration applied to the processing of each individual message.
        /// If the handler does not complete within the timeout, it is treated as a failed message.
        /// </summary>
        public TimeSpan? Timeout
        {
            get => _timeout;
            set
            {
                if (value.HasValue && value.Value < TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(Timeout), "Timeout must not be negative.");
                }
                _timeout = value;
            }
        }

        private TimeSpan? _timeout;

        /// <summary>
        /// Optional timeout for the whole run, acting as a safety net against runs that can never finish
        /// (e.g. messages lost to a broker failure). When it elapses, in-progress handlers are canceled
        /// cooperatively, pending messages are reported as failed, and the run completes with the partial result.
        /// Unlike <see cref="Timeout"/>, which applies per message, this bounds the total duration of the run.
        /// </summary>
        public TimeSpan? RunTimeout
        {
            get => _runTimeout;
            set
            {
                if (value.HasValue && value.Value <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(RunTimeout), "RunTimeout must be greater than zero.");
                }
                _runTimeout = value;
            }
        }

        private TimeSpan? _runTimeout;

        /// <summary>
        /// Optional backpressure for asynchronous sources (<c>IAsyncEnumerable&lt;T&gt;</c>): the maximum number of
        /// messages that have been pulled from the source but have not yet reached a terminal state (processed,
        /// failed, or failed to publish). When the limit is reached the run stops pulling from the source until a
        /// handler finishes, so the pause propagates naturally to the producer (a bounded channel writer, a database
        /// cursor, a paged API). Without it the temporary queue absorbs the whole source at publish speed.
        /// <br/>
        /// Must be at least <see cref="PrefetchCount"/>, otherwise handlers could never reach their configured
        /// concurrency. Null (default) keeps the unbounded behavior. Ignored by the collection overloads, whose
        /// input already lives in memory.
        /// <br/>
        /// Sizing: start at 2× to 4× <see cref="PrefetchCount"/> (e.g. 4 handlers → 8–16 in flight). Raise it
        /// when the source delivers in high-latency batches (cover one or two pages); lower it towards
        /// <see cref="PrefetchCount"/> when messages are large or losing them on a connection drop matters more
        /// than throughput, since everything in the temporary queue is lost on a drop. Hundreds or thousands
        /// defeat the purpose. See docs/temporary-processing.md, "Choosing a value".
        /// </summary>
        public int? MaxInFlightMessages
        {
            get => _maxInFlightMessages;
            set
            {
                if (value.HasValue && value.Value < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(MaxInFlightMessages), "MaxInFlightMessages must be greater than 0.");
                }
                _maxInFlightMessages = value;
            }
        }

        private int? _maxInFlightMessages;

        /// <summary>
        /// Optional callback that receives a <see cref="TemporaryRunProgress"/> snapshot every
        /// <see cref="ProgressInterval"/> while the run is in progress, so the caller can persist it wherever other
        /// processes can read it (a cache, a database) and observe the run from outside. Keying the stored snapshot
        /// by <see cref="CorrelationId"/> lets any instance of the service find it.
        /// <br/>
        /// It is called on every interval even when nothing changed, which makes it a heartbeat: a stored
        /// <see cref="TemporaryRunProgress.TimestampUtc"/> that stops advancing means the run or its process is gone.
        /// Calls never overlap (a slow callback delays the next one instead of piling up), never block message
        /// processing, and an exception is logged without affecting the run. No new call starts once the run begins
        /// to close; the final figures come from the completion callback. The token is the run's own and is canceled
        /// when the run ends: honor it, a call still running when the run closes is waited for at most 5 seconds.
        /// Null (default) disables progress reporting and adds no overhead.
        /// </summary>
        public Func<TemporaryRunProgress, CancellationToken, Task>? OnProgress { get; set; }

        /// <summary>
        /// How often <see cref="OnProgress"/> is called, measured from the end of the previous call. Default 5 seconds.
        /// Ignored when <see cref="OnProgress"/> is null.
        /// </summary>
        public TimeSpan ProgressInterval
        {
            get => _progressInterval;
            set
            {
                if (value <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(ProgressInterval), "ProgressInterval must be greater than zero.");
                }
                _progressInterval = value;
            }
        }

        private TimeSpan _progressInterval = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Optional prefix used to customize the generated queue name.
        /// </summary>
        public string? QueuePrefixName { get; set; }

        public static RunTemporaryOptions Default => new RunTemporaryOptions();

    }
}