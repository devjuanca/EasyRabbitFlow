## Observability

EasyRabbitFlow is OpenTelemetry-ready out of the box: spans through an `ActivitySource` and metrics through a `Meter`, both named `EasyRabbitFlow`, plus a native health check. Everything is zero-cost until a listener subscribes. Instrumentation covers publishing, consuming, and the dead-letter path; [temporary batch runs](temporary-processing.md) are the exception — they emit no spans or metrics, and report through their `TemporaryRunResult` instead.

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(RabbitFlowDiagnostics.ActivitySourceName)
        .AddAspNetCoreInstrumentation())
    .WithMetrics(metrics => metrics
        .AddMeter(RabbitFlowDiagnostics.MeterName)
        .AddAspNetCoreInstrumentation())
    .UseOtlpExporter();
```

### Distributed Tracing

EasyRabbitFlow propagates the **W3C trace context** (`traceparent` / `tracestate`) as AMQP headers — so a trace started in an HTTP request continues through the broker into the consumer, across service boundaries.

What you get:

- **`{destination} publish`** (`Producer` span) per `PublishAsync` / `PublishBatchAsync`, tagged with `messaging.system=rabbitmq`, destination, routing key, `messaging.message.id`, and `messaging.message.conversation_id` (the correlation id). Failed publishes set error status.
- **`{queue} process`** (`Consumer` span) per delivered message, parented to the publisher's remote context extracted from the `traceparent` header. Your handler runs inside this span, so any spans you create (HTTP calls, DB queries) nest under it. Exhausted retries set error status.

Notes:

- If there is an ambient `Activity` (e.g. an ASP.NET Core request) when publishing, its context is propagated in headers **even without** an OpenTelemetry listener — consumers on other services can still continue the trace.
- Caller-supplied `traceparent` / `tracestate` entries in `PublishOptions.Headers` are respected and never overwritten.
- Messages replayed by the [Dead-Letter Reprocessor](dead-letter.md#dead-letter-reprocessor) keep their original `traceparent` (captured in the envelope), so the replay links back to the original trace.
- This complements (does not replace) `CorrelationId`, which remains a first-class AMQP property.

Span attributes follow the OpenTelemetry messaging conventions: `messaging.system`, `messaging.operation.type`
(`send` / `process`) and `messaging.operation.name` (plus the legacy `messaging.operation`), `messaging.destination.name`,
`messaging.rabbitmq.destination.routing_key`, `messaging.message.id`, `messaging.message.conversation_id`,
`messaging.batch.message_count`, and on the consumer side `messaging.rabbitmq.message.delivery_tag`,
`messaging.rabbitmq.message.redelivered` and `messaging.rabbitmq.message.delivery_count` (quorum queues).
A failed publish or delivery sets the span status to error, the `error.type` attribute (the exception's full type name)
and an `exception` event with `exception.type`, `exception.message` and `exception.stacktrace`, so the failure shows up
with its stack in the tracing backend (v8.4.0).

### Logging

All library log lines start with `[RABBIT-FLOW]:` and use structured templates (`{Queue}`, `{Consumer}`, `{MessageId}`, …),
so a log platform can filter on the prefix and facet on the fields. Levels:

| Level | What |
|---|---|
| `Error` | A delivery that is dead-lettered (handler exception after the last attempt, deserialization failure, abandoned handler), a dead-letter envelope that could not be written, a recovery that keeps failing (third attempt onwards), a temporary-run handler that throws, broker callback exceptions |
| `Warning` | A retried attempt (transient failure or cooperative timeout), a poison message given up on, a broker-initiated consumer cancel, channel/connection shutdown, the first recovery attempts, topology adopted with different arguments, per-message timeouts and cancellations inside a temporary run |
| `Information` | Startup facts (reprocessor enabled, parking queue created), a successful recovery, reprocessor cycle summaries |
| `Debug` | Each publish, reprocessor details |

**Every delivery is processed inside a log scope** carrying `Queue`, `Consumer`, `MessageId`, `CorrelationId` and
`DeliveryTag` (v8.4.0). Logging providers that render scopes (Serilog with `IncludeScopes`, the Datadog and OpenTelemetry
log enrichers, the console logger with `IncludeScopes = true`) attach those fields to **the handler's own log lines** as well
as the library's, so a handler's `logger.LogWarning("payment declined")` can be tied back to its queue and message without
a tracer. The terminal failure line names the reason (`handler_error`, `timeout`, `abandoned`, `poison`, `deserialize`),
the attempt count, `Redelivered`, `DeliveryCount` and `ReprocessAttempts`, and carries the exception.

### Metrics

| Instrument | Type | Tags | Description |
|------------|------|------|-------------|
| `easyrabbitflow.messages.published` | Counter | `destination` | Messages successfully published (batches count each message) |
| `easyrabbitflow.messages.publish_failures` | Counter | `destination` | Failed publish operations |
| `easyrabbitflow.messages.consumed` | Counter | `queue`, `outcome`, `reason` | Messages reaching a terminal consume state (`success` / `failure`). On failures, `reason` says why: `handler_error`, `timeout` (cooperative handler timeout), `abandoned` (stuck-handler watchdog), `poison` (`MaxBrokerDeliveries` reached), `deserialize` (v8.4.0) |
| `easyrabbitflow.messages.retried` | Counter | `queue` | In-process retry attempts (transient failures / per-attempt timeouts) |
| `easyrabbitflow.messages.dead_lettered` | Counter | `queue` | Messages routed to a dead-letter queue |
| `easyrabbitflow.messages.reprocessed` | Counter | `queue` | Messages re-enqueued from the DLQ by the reprocessor |
| `easyrabbitflow.messages.parked` | Counter | `queue`, `reason` | Messages moved to the parking queue (`exhausted` / `permanent` / `malformed`) |
| `easyrabbitflow.messages.discarded` | Counter | `queue`, `reason` | Messages dropped by the reprocessor instead of parked when `FinalAction = Discard` (`exhausted` / `permanent`) |
| `easyrabbitflow.messages.poisoned` | Counter | `queue` | Deliveries dead-lettered without invoking the handler because the broker had already returned them `MaxBrokerDeliveries` times (`x-delivery-count`, quorum queues) |
| `easyrabbitflow.consumer.cancellations` | Counter | `queue` | Broker-initiated consumer cancellations (`basic.cancel` with the channel still open: queue deleted, node gone, or a quorum-queue consumer timeout on RabbitMQ 4.3+) that triggered a re-subscribe |
| `easyrabbitflow.consumer.message.duration` | Histogram (s) | `queue`, `outcome` | End-to-end processing time per delivery, including in-process retries |

A handler abandoned by the [stuck-handler watchdog](consumers.md#consumer-timeout) shows up as
`messages.consumed{outcome=failure, reason=abandoned}` plus `messages.dead_lettered`, with an `Error` log line and a
`RabbitFlowHandlerAbandonedException` in the dead-letter envelope.

### Health Check

A native `IHealthCheck` integrates with the standard health checks pipeline, built on the single-round-trip [queue state inspection](queue-operations.md#queue-state-inspection):

```csharp
builder.Services.AddHealthChecks()
    .AddRabbitFlow(configure: o =>
    {
        o.Queues.Add("orders-queue");        // must exist → otherwise Unhealthy
        o.MaxReadyMessages = 10_000;          // backlog above this → Degraded
        o.RequireConsumers = true;            // queue without consumers → Degraded
    });

// later:
app.MapHealthChecks("/health");
```

With no queues configured it acts as a pure broker-connectivity probe. Per-queue counts are exposed in the health report's `Data` dictionary (`{queue}.exists`, `{queue}.messages`, `{queue}.consumers`).

Notes:

- A configured queue that does not exist makes the check **Unhealthy**; the backlog and consumer rules are then skipped for that queue (there is nothing to measure).
- The standard `failureStatus` parameter of `AddRabbitFlow(...)` is honored if you want failures reported as something other than `Unhealthy`.
