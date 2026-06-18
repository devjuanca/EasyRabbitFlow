## Observability

EasyRabbitFlow is OpenTelemetry-ready out of the box: spans through an `ActivitySource` and metrics through a `Meter`, both named `EasyRabbitFlow`, plus a native health check. Everything is zero-cost until a listener subscribes.

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

### Metrics

| Instrument | Type | Tags | Description |
|------------|------|------|-------------|
| `easyrabbitflow.messages.published` | Counter | `destination` | Messages successfully published (batches count each message) |
| `easyrabbitflow.messages.publish_failures` | Counter | `destination` | Failed publish operations |
| `easyrabbitflow.messages.consumed` | Counter | `queue`, `outcome` | Messages reaching a terminal consume state (`success` / `failure`) |
| `easyrabbitflow.messages.retried` | Counter | `queue` | In-process retry attempts (transient failures / per-attempt timeouts) |
| `easyrabbitflow.messages.dead_lettered` | Counter | `queue` | Messages routed to a dead-letter queue |
| `easyrabbitflow.messages.reprocessed` | Counter | `queue` | Messages re-enqueued from the DLQ by the reprocessor |
| `easyrabbitflow.messages.parked` | Counter | `queue`, `reason` | Messages moved to the parking queue (`exhausted` / `permanent` / `malformed`) |
| `easyrabbitflow.consumer.message.duration` | Histogram (s) | `queue`, `outcome` | End-to-end processing time per delivery, including in-process retries |

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
