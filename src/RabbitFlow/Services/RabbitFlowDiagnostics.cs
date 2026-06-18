using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;

namespace EasyRabbitFlow.Services
{
    /// <summary>
    /// Observability entry point for EasyRabbitFlow.
    /// Register <see cref="ActivitySourceName"/> with your tracing provider (e.g. OpenTelemetry's <c>AddSource</c>)
    /// to collect publish and process spans, and <see cref="MeterName"/> with your metrics provider
    /// (e.g. <c>AddMeter</c>) to collect message counters and processing-duration histograms.
    /// Trace context is propagated through the standard W3C <c>traceparent</c> / <c>tracestate</c>
    /// AMQP headers, so traces continue across services even when only one side uses EasyRabbitFlow.
    /// </summary>
    public static class RabbitFlowDiagnostics
    {
        /// <summary>
        /// Name of the <see cref="ActivitySource"/> used by EasyRabbitFlow.
        /// </summary>
        public const string ActivitySourceName = "EasyRabbitFlow";

        /// <summary>
        /// Name of the <see cref="Meter"/> used by EasyRabbitFlow.
        /// </summary>
        public const string MeterName = "EasyRabbitFlow";

        internal const string TraceParentHeader = "traceparent";

        internal const string TraceStateHeader = "tracestate";

        private static readonly string AssemblyVersion = typeof(RabbitFlowDiagnostics).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        internal static readonly ActivitySource Source = new ActivitySource(ActivitySourceName, AssemblyVersion);

        internal static readonly Meter Meter = new Meter(MeterName, AssemblyVersion);

        internal static readonly Counter<long> PublishedMessages = Meter.CreateCounter<long>(
            "easyrabbitflow.messages.published", "{message}", "Messages successfully published to the broker.");

        internal static readonly Counter<long> PublishFailures = Meter.CreateCounter<long>(
            "easyrabbitflow.messages.publish_failures", "{message}", "Publish operations that failed.");

        internal static readonly Counter<long> ConsumedMessages = Meter.CreateCounter<long>(
            "easyrabbitflow.messages.consumed", "{message}", "Messages that reached a terminal consume state (tag 'outcome' = success | failure).");

        internal static readonly Counter<long> RetriedMessages = Meter.CreateCounter<long>(
            "easyrabbitflow.messages.retried", "{message}", "In-process retry attempts triggered by transient failures or per-attempt timeouts.");

        internal static readonly Counter<long> DeadLetteredMessages = Meter.CreateCounter<long>(
            "easyrabbitflow.messages.dead_lettered", "{message}", "Messages routed to a dead-letter queue after exhausting attempts.");

        internal static readonly Counter<long> ReprocessedMessages = Meter.CreateCounter<long>(
            "easyrabbitflow.messages.reprocessed", "{message}", "Messages re-enqueued from the dead-letter queue back to the main queue.");

        internal static readonly Counter<long> ParkedMessages = Meter.CreateCounter<long>(
            "easyrabbitflow.messages.parked", "{message}", "Messages moved to the parking queue (tag 'reason' = exhausted | permanent | malformed).");

        internal static readonly Histogram<double> ConsumeDuration = Meter.CreateHistogram<double>(
            "easyrabbitflow.consumer.message.duration", "s", "End-to-end processing time per delivered message, including in-process retries.");

        /// <summary>
        /// True when there is an ambient W3C activity whose context should be propagated,
        /// regardless of whether a listener is attached to this source.
        /// </summary>
        internal static bool HasCurrentW3CContext => Activity.Current?.IdFormat == ActivityIdFormat.W3C && Activity.Current.Id != null;

        internal static Activity? StartPublish(string destination, string routingKey, string? messageId, string? correlationId, int? batchCount = null)
        {
            var activity = Source.StartActivity($"{destination} publish", ActivityKind.Producer);

            if (activity != null)
            {
                activity.SetTag("messaging.system", "rabbitmq");
                activity.SetTag("messaging.operation", "publish");
                activity.SetTag("messaging.destination.name", destination);

                if (!string.IsNullOrEmpty(routingKey))
                {
                    activity.SetTag("messaging.rabbitmq.destination.routing_key", routingKey);
                }

                if (messageId != null)
                {
                    activity.SetTag("messaging.message.id", messageId);
                }

                if (correlationId != null)
                {
                    activity.SetTag("messaging.message.conversation_id", correlationId);
                }

                if (batchCount.HasValue)
                {
                    activity.SetTag("messaging.batch.message_count", batchCount.Value);
                }
            }

            return activity;
        }

        internal static Activity? StartProcess(string queueName, IDictionary<string, object?>? headers, string? messageId, string? correlationId, bool redelivered)
        {
            var parentContext = ExtractContext(headers);

            var activity = Source.StartActivity($"{queueName} process", ActivityKind.Consumer, parentContext);

            if (activity != null)
            {
                activity.SetTag("messaging.system", "rabbitmq");
                activity.SetTag("messaging.operation", "process");
                activity.SetTag("messaging.destination.name", queueName);

                if (messageId != null)
                {
                    activity.SetTag("messaging.message.id", messageId);
                }

                if (correlationId != null)
                {
                    activity.SetTag("messaging.message.conversation_id", correlationId);
                }

                if (redelivered)
                {
                    activity.SetTag("messaging.rabbitmq.message.redelivered", true);
                }
            }

            return activity;
        }

        /// <summary>
        /// Writes the current W3C trace context into AMQP headers so consumers can continue the trace.
        /// Caller-supplied <c>traceparent</c> / <c>tracestate</c> headers are respected.
        /// </summary>
        internal static void InjectContext(IDictionary<string, object?> headers)
        {
            var current = Activity.Current;

            if (current == null || current.IdFormat != ActivityIdFormat.W3C || current.Id == null)
            {
                return;
            }

            if (!headers.ContainsKey(TraceParentHeader))
            {
                headers[TraceParentHeader] = current.Id;

                if (!string.IsNullOrEmpty(current.TraceStateString) && !headers.ContainsKey(TraceStateHeader))
                {
                    headers[TraceStateHeader] = current.TraceStateString;
                }
            }
        }

        internal static ActivityContext ExtractContext(IDictionary<string, object?>? headers)
        {
            var traceparent = ReadHeaderString(headers, TraceParentHeader);

            if (traceparent == null)
            {
                return default;
            }

            var tracestate = ReadHeaderString(headers, TraceStateHeader);

            return ActivityContext.TryParse(traceparent, tracestate, isRemote: true, out var context) ? context : default;
        }

        private static string? ReadHeaderString(IDictionary<string, object?>? headers, string name)
        {
            if (headers == null || !headers.TryGetValue(name, out var raw) || raw == null)
            {
                return null;
            }

            return raw switch
            {
                string text => text,
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                _ => null,
            };
        }
    }
}
