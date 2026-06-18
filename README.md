<p align="center">
  <img src="icon.png" alt="EasyRabbitFlow" width="120" />
</p>

<h1 align="center">EasyRabbitFlow</h1>

<p align="center">
  <strong>High-performance RabbitMQ client for .NET — fluent configuration, automatic topology, zero-ceremony consumers.</strong>
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/EasyRabbitFlow"><img src="https://img.shields.io/nuget/v/EasyRabbitFlow" alt="NuGet" /></a>
  <a href="https://www.nuget.org/packages/EasyRabbitFlow"><img src="https://img.shields.io/nuget/dt/EasyRabbitFlow" alt="Downloads" /></a>
  <a href="LICENSE.txt"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="License" /></a>
</p>


---

## What's New in v8.0

- **OpenTelemetry-ready observability** — `ActivitySource` spans with W3C trace-context propagation across the broker, a `Meter` with message counters and a processing-duration histogram, and a native health check. See [Observability](docs/observability.md).
- **Richer temporary batch processing** — `RunAsync` now returns a `TemporaryRunResult` with full counters and per-error detail, supports a whole-run `RunTimeout`, and ends early (instead of hanging) when the broker connection drops. See [Temporary Batch Processing](docs/temporary-processing.md).
- **Hardened dead-letter path** — failed messages are written with **publisher confirms** (never silently lost), the envelope captures and restores the original AMQP properties on replay, and the reprocessor parks non-reprocessable messages instead of churning the DLQ. See [Dead-Letter Handling](docs/dead-letter.md).
- **Fixed-interval, transient-only retry** — the in-handler `RetryPolicy` is now a constant `RetryInterval` scoped to short transient failures (exponential backoff removed); long recovery windows belong to the reprocessor. See [Retry Policies](docs/consumers.md#retry-policies) and [Transient Exceptions](docs/transient-exceptions.md).
- **Queue state inspection & health check** — a single-round-trip `QueueState` snapshot API and an `AddHealthChecks().AddRabbitFlow()` probe. See [Queue Operations](docs/queue-operations.md).
- **Multi-targeting** — `netstandard2.1` (works on .NET 6/7+) and `net8.0`.

See the [Breaking Changes](#️-breaking-changes-v800) section below before upgrading.

---

## Documentation

| Guide | Contents |
|-------|----------|
| [Getting Started](#getting-started) | Installation and a four-step quick start |
| [Configuration](docs/configuration.md) | Host settings, JSON serialization, publisher options |
| [Consumers](docs/consumers.md) | Implementing & registering consumers, auto-generate topology, retry policies, consumer timeout, message context |
| [Dead-Letter Handling](docs/dead-letter.md) | Delivery guarantee, replicas, reprocessor, manual replay safety net |
| [Publishing Messages](docs/publishing.md) | Single & batch publishing, idempotency, correlation, per-call AMQP options |
| [Queue Operations](docs/queue-operations.md) | Queue state inspection and purging |
| [Temporary Batch Processing](docs/temporary-processing.md) | Fire-and-forget batch workflows with auto-cleanup |
| [Observability](docs/observability.md) | Distributed tracing, metrics, health check |
| [Transient Exceptions](docs/transient-exceptions.md) | Transient vs. permanent failures and custom retry logic |
| [Sample Project](docs/sample-project.md) | Runnable sample API and Aspire AppHost |
| [API Reference](docs/api-reference.md) | Registered services, extension methods, performance notes |

---

## Why EasyRabbitFlow?

| Feature | EasyRabbitFlow |
|---------|----------------|
| Fluent, strongly-typed configuration | ✅ |
| Automatic queue / exchange / dead-letter generation | ✅ |
| Reflection-free per-message processing | ✅ |
| Configurable in-process retry for transient failures | ✅ |
| Temporary batch processing with auto-cleanup | ✅ |
| Queue state & purge utilities | ✅ |
| Full DI integration (scoped/transient/singleton) | ✅ |
| Publisher confirms (single) & transactional batch | ✅ |
| Built-in `MessageId` metadata on every message (auto-GUID or caller-supplied deterministic key) | ✅ |
| CorrelationId support (end-to-end tracing) | ✅ |
| `ActivitySource` spans + W3C trace context propagation (OpenTelemetry-ready) | ✅ |
| Built-in metrics (`Meter`) — counters + processing-duration histogram | ✅ |
| Native health check (`AddHealthChecks().AddRabbitFlow()`) | ✅ |
| Graceful shutdown: bounded drain of in-flight handlers before closing channels | ✅ |
| `RabbitFlowMessageContext` per-message metadata | ✅ |
| Rich `PublishResult` / `BatchPublishResult` types | ✅ |
| Thread-safe channel operations | ✅ |
| Multi-targeted: .NET Standard 2.1 (works with .NET 6, 7+) and .NET 8 (used by .NET 8, 9+) | ✅ |

---

## Public Services

EasyRabbitFlow registers a small set of services through `AddRabbitFlow(...)`. Inject any of them anywhere in your app via DI.

| Service | Lifetime | What it does |
|---------|----------|--------------|
| [`IRabbitFlowPublisher`](docs/publishing.md) | Singleton | Publishes single messages (with publisher confirms) or batches (atomic transactional / individually confirmed). Returns a rich `PublishResult` / `BatchPublishResult`, and supports `MessageId`, `CorrelationId`, and per-call AMQP options. |
| [`IRabbitFlowConsumer<TEvent>`](docs/consumers.md) | — | The interface **you** implement. Each consumer's `HandleAsync` receives the deserialized event, a `RabbitFlowMessageContext` (metadata), and a `CancellationToken`. Registered with `AddConsumer<TConsumer>(...)` and run by the hosted service. |
| [`IRabbitFlowTemporary`](docs/temporary-processing.md) | Singleton | Fire-and-forget batch workflows over a throwaway queue that is created and torn down automatically. `RunAsync` returns a `TemporaryRunResult` with full counters and per-error detail. |
| [`IRabbitFlowState`](docs/queue-operations.md) | Singleton | Single-round-trip queue inspection — message and consumer counts, existence — for one queue (`GetQueueStateAsync`) or many (`GetQueuesStateAsync`). |
| [`IRabbitFlowPurger`](docs/queue-operations.md) | Singleton | Empties a queue of all its messages. |
| [`ConsumerHostedService`](docs/consumers.md) | Hosted | Background service (started by `UseRabbitFlowConsumers()`) that owns the lifecycle of every registered consumer: declares topology, dispatches messages, applies retry policies, dead-letters failures, and drains in-flight handlers on shutdown. |

See the [API Reference](docs/api-reference.md) for extension methods, configurator options, and observability constants.

---

## Getting Started

### Installation

```bash
dotnet add package EasyRabbitFlow
```

### Quick Start

**1. Define your event model:**

```csharp
public class OrderCreatedEvent
{
    public string OrderId { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

**2. Implement a consumer:**

```csharp
public class OrderConsumer : IRabbitFlowConsumer<OrderCreatedEvent>
{
    private readonly ILogger<OrderConsumer> _logger;

    public OrderConsumer(ILogger<OrderConsumer> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(OrderCreatedEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing order {OrderId}, total: {Total}, correlationId: {CorrelationId}",
            message.OrderId, message.Total, context.CorrelationId);

        // Your business logic here: save to DB, send email, call API, etc.
        await Task.CompletedTask;
    }
}
```

**3. Configure in `Program.cs`:**

```csharp
builder.Services
    .AddRabbitFlow(cfg =>
    {
        cfg.ConfigureHost(host =>
        {
            host.Host = "localhost";
            host.Port = 5672;
            host.Username = "guest";
            host.Password = "guest";
        });

        cfg.AddConsumer<OrderConsumer>("orders-queue", c =>
        {
            c.AutoGenerate = true;
            c.PrefetchCount = 10;
            c.Timeout = TimeSpan.FromSeconds(30);
            c.ConfigureRetryPolicy(r =>
            {
                r.MaxRetryCount = 3;
                r.RetryInterval = 1000;
            });
        });
    })
    .UseRabbitFlowConsumers(); // Starts background consumer automatically
```

**4. Publish from an endpoint or service:**

```csharp
app.MapPost("/orders", async (OrderCreatedEvent order, IRabbitFlowPublisher publisher) =>
{
    var result = await publisher.PublishAsync(order, "orders-queue");
    return result.Success
        ? Results.Ok(new { result.Destination, result.MessageId, result.TimestampUtc })
        : Results.Problem(result.Error?.Message);
});
```

That's it — **four steps** from zero to a working publish/consume pipeline.

---

## ⚠️ Breaking Changes (v8.0.0)

**`IRabbitFlowTemporary.RunAsync` now returns `TemporaryRunResult` instead of `int`**

All three overloads return a rich result (`TotalMessages`, `PublishedMessages`, `ProcessedMessages`, `SucceededMessages`, `FailedMessages`, `Success`, `Duration`, `Errors`); the `<T, TResult>` overload returns `TemporaryRunResult<TResult>` with a `Results` collection. See [Temporary Batch Processing](docs/temporary-processing.md#temporary-batch-processing).

**Migration:** replace `int processed = await temporary.RunAsync(...)` with `var run = await temporary.RunAsync(...)` and read `run.ProcessedMessages` (or the richer counters). The `onCompleted` / `onCompletedAsync` callbacks are unchanged.

**`IRabbitFlowState` has two new interface members**

`GetQueueStateAsync` and `GetQueuesStateAsync` were added (see [Queue State Inspection](docs/queue-operations.md#queue-state-inspection)). No change for callers; custom implementations of the interface (e.g. test mocks) must add the new members.

**Transient classification broadened (behavior change)**

The in-process retry policy, the dead-letter envelope, and the reprocessor now share one inheritance-aware classifier: subclasses of `RabbitFlowTransientException`, `TimeoutException`, and transient HTTP failures (`HttpRequestException` with no response or status 408/429/502/503/504) are retried automatically — including through the inner-exception chain. Previously these required manual wrapping; messages that used to go straight to the DLQ may now be retried first. See [Transient Exceptions](docs/transient-exceptions.md#transient-exceptions-and-custom-retry-logic).

**Dead-letter reprocessor parks non-reprocessable messages (behavior change)**

Exhausted, permanent, and malformed messages are moved once to `{queue}-deadletter-parking` (declared durable by the reprocessor) instead of rotating through the DLQ on every cycle. Tooling or operators that inspected those messages in the DLQ should look at the parking queue instead. See [Dead-Letter Reprocessor](docs/dead-letter.md#dead-letter-reprocessor).

**Retry policy is now a fixed interval — exponential backoff removed**

`RetryPolicy<TConsumer>` no longer exposes `ExponentialBackoff`, `ExponentialBackoffFactor`, or `MaxRetryDelay`. The in-handler retry is intentionally scoped to short, ephemeral transient failures, so it now always waits a constant `RetryInterval` between attempts. For failures that take minutes to resolve, use the [dead-letter reprocessor](docs/dead-letter.md#dead-letter-reprocessor), which retries on a slow recovery cadence without holding the delivery unacknowledged. See [Retry Policies](docs/consumers.md#retry-policies).

**Migration:** delete `ExponentialBackoff`, `ExponentialBackoffFactor`, and `MaxRetryDelay` from your `ConfigureRetryPolicy(...)` calls (they no longer compile). Keep `MaxRetryCount` and `RetryInterval`. If you relied on long backoff windows, move that workload to the dead-letter reprocessor instead.

**Other changes (non-breaking but notable)**

- Multi-targeting: `netstandard2.1` + `net8.0`.
- OpenTelemetry-ready observability: `ActivitySource` spans with W3C trace-context propagation, a `Meter` with message counters and duration histograms, and a native health check. See [Observability](docs/observability.md#observability).
- Temporary runs support a whole-run `RunTimeout` and end early (with a `ConnectionLost` error entry) when the broker connection drops, instead of hanging.
- `onError` in temporary runs now also fires for publish failures, and `TemporaryRunError.MessageIndex` identifies the failed input message.
- The dead-letter envelope captures the original AMQP properties (`DeliveryMode`, headers, `Type`, `AppId`, …) and the reprocessor restores them on replay — persistent messages stay persistent.
- Consumers drain in-flight handlers (bounded) during shutdown before closing channels, avoiding redeliveries after deploys.

---

## ⚠️ Breaking Changes (v7.0.0)

**`ConfigureCustomDeadletter` and `CustomDeadLetterSettings<TConsumer>` removed**

The custom dead-letter feature has been replaced by `AutoGenerateSettings<TConsumer>.DeadLetterReplicas` (see [Dead-Letter Replicas](docs/dead-letter.md#dead-letter-replicas)). The new mechanism is strictly more capable:

- Replicates the dead-lettered message to any number of extra queues, not just one.
- Catches **all** failure modes — including messages that fail to deserialize. The old feature only fired when the message was successfully deserialized.
- Declares and binds the extra queues itself; no external topology setup required.
- Routes through the dead-letter exchange instead of issuing an extra publish call, so each replica queue receives the same payload as the primary DLQ (`DeadLetterEnvelope` when `ExtendDeadletterMessage = true`, raw body otherwise).

**Migration:** replace

```csharp
c.ConfigureCustomDeadletter(dl =>
{
    dl.DeadletterQueueName = "custom-errors-queue";
});
```

with

```csharp
c.AutoGenerate = true;                  // required
c.ConfigureAutoGenerate(ag =>
{
    ag.GenerateDeadletterQueue = true;  // required
    ag.DeadLetterReplicas.Add(new DeadLetterReplica
    {
        QueueName = "custom-errors-queue"
    });
});
```

If you previously consumed `"custom-errors-queue"` expecting the raw deserialized event, switch to consuming the `DeadLetterEnvelope` shape (or set `ExtendDeadletterMessage = false` if you only need the original body, at the cost of losing the error metadata on the primary DLQ).

**Dead-letter envelope and reprocessor publications now route through the named DLX**

Previously, the manual `DeadLetterEnvelope` publish from the consumer's error handler and the reprocessor's re-publish to the DLQ used the default exchange (`""`) with the queue name as routing key. They now publish via `{queueName}-deadletter-exchange` / `{queueName}-deadletter-routing-key` — the same path RabbitMQ uses for native dead-lettering.

**Migration:** no code change is required if you only consume the DLQ. The destination queue is identical and routing is transparent. This change is required for `DeadLetterReplicas` to work uniformly: extra queues bound to the DLX now receive a copy of every dead-lettered message, regardless of whether it arrived via native nack-driven dead-lettering or via the manual envelope path.

**`PublisherOptions` renamed to `PublisherConnectionOptions`**

Cosmetic rename to avoid the one-letter visual collision with the new per-call `PublishOptions` type (see next entry). The shape is the same as before, plus the new `PublisherId` property (see below).

**Migration:** rename the type at the call site. If you only use the `pub => ...` lambda form, you won't see the change.

```diff
- cfg.ConfigurePublisher((PublisherOptions pub) =>
+ cfg.ConfigurePublisher((PublisherConnectionOptions pub) =>
  {
      pub.DisposePublisherConnection = false;
  });
```

**`PublishAsync` / `PublishBatchAsync`: `publisherId` and `jsonOptions` parameters replaced by `PublishOptions? options`**

The four publish overloads used to take `string publisherId = ""` and `JsonSerializerOptions? jsonOptions = null` as the last two parameters before `cancellationToken`. Both are gone, replaced by a single `PublishOptions? options = null` parameter that also exposes per-call AMQP metadata (`DeliveryMode`, `Headers`, `Type`, `AppId`, `Expiration`, `Priority`, `Timestamp`, `ReplyTo`, `ContentType`).

- `jsonOptions` per-call moved to `PublishOptions.JsonOptions`.
- `publisherId` is no longer per-call — it is now configured once via `PublisherConnectionOptions.PublisherId`. The previous per-call behavior was misleading: since the publisher caches a single connection internally, only the first call's value ever shaped the connection name; subsequent calls reused the cached connection regardless of what was passed.

**Migration:**

```diff
- await publisher.PublishAsync(order, "orders-queue",
-     correlationId: requestId,
-     publisherId: "checkout",
-     jsonOptions: customJson);
+
+ // Once, at startup:
+ cfg.ConfigurePublisher(pub => pub.PublisherId = "checkout");
+
+ // Per call:
+ await publisher.PublishAsync(order, "orders-queue",
+     correlationId: requestId,
+     options: new PublishOptions { JsonOptions = customJson });
```

Callers using only `messageId:` / `correlationId:` named arguments are unaffected. Positional callers, and any callers using `publisherId:` or `jsonOptions:` named arguments, must update.

---

## ⚠️ Breaking Changes (v6.0.0)

**`PublisherOptions.IdempotencyEnabled` removed**

Every published message now carries a `MessageId` unconditionally. The publisher no longer offers a knob to disable identification — it was a misnamed footgun (auto-GUID per call doesn't actually provide idempotency anyway, since retries get a new GUID).

**Migration:** delete the line. Behavior is now what `IdempotencyEnabled = true` used to do.

 ```diff

cfg.ConfigurePublisher(pub =>
 {
     pub.DisposePublisherConnection = false;
    pub.IdempotencyEnabled = true;
 });
 ```

**`PublishAsync` gained a `messageId` parameter; `PublishBatchAsync` gained a `messageIdSelector` parameter**

For real publish-side idempotency (deterministic key derived from business data), pass the value yourself:

``` csharp
// Single — caller supplies the key
await publisher.PublishAsync(order, "orders-queue", messageId: $"order-{order.Id}-created");

// Batch — selector produces a key per event
await publisher.PublishBatchAsync(orders, "orders-queue", messageIdSelector: o => $"order-{o.Id}-created");
```

If you don't pass anything, you still get a unique GUID per message — same as before with `IdempotencyEnabled = true`.

**`PublishResult.MessageId` is now `string` (non-nullable)**

Always populated. If you had `if (result.MessageId != null) ...`, it's now dead code; just use `result.MessageId` directly.

**Positional argument breakage**

`PublishAsync` and `PublishBatchAsync` shifted parameters to insert `messageId` / `messageIdSelector` before `correlationId`. Callers using **named** arguments (`correlationId:`, `routingKey:`, etc.) are unaffected. Positional callers must update.

**`IServiceProvider.InitializeConsumerAsync<TEvent, TConsumer>` removed**

The manual consumer-bootstrap extension — deprecated since v4 in favor of the hosted service registered by `UseRabbitFlowConsumers()` — is gone. All consumers are now started exclusively by the hosted service.

**Migration:** delete any `InitializeConsumerAsync` calls from `Program.cs`/`Startup.cs` and ensure your RabbitFlow registration ends with `.UseRabbitFlowConsumers()`. The hosted service picks up every consumer added via `settings.AddConsumer<TConsumer>(...)` automatically.

```diff
- await app.Services.InitializeConsumerAsync<OrderEvent, OrderConsumer>();
- await app.Services.InitializeConsumerAsync<EmailEvent, EmailConsumer>();
+ // nothing to do — UseRabbitFlowConsumers() in your service registration handles it
```

`ConsumerRegisterSettings` (the options bag taken by that extension) is no longer reachable from public API surface.

**`RetryPolicy.MaxRetryCount` semantics changed — now counts retries, not attempts**

Previously `MaxRetryCount = N` meant "process at most N times" (so `1` was the weird "process once, never retry" default). It now counts **retries after the initial attempt fails**:

- `0` — no retries (1 attempt total). This is the new default.
- `1` — one retry after the first failure (2 attempts total).
- `3` — three retries after the first failure (4 attempts total).

**Migration:** any consumer that explicitly set `MaxRetryCount = N` will now perform one extra attempt. To preserve the old behavior, change `MaxRetryCount = N` to `MaxRetryCount = N - 1`. Consumers relying on the default get a behavior change too — the previous default `1` (one attempt, no retry) and the new default `0` are equivalent, so no action needed there. The auto-generated queue's `x-consumer-timeout` follows the new formula `Timeout × (MaxRetryCount + 1) + Σ RetryIntervals + 30s grace`.

**`ConsumerSettings.ExtendDeadletterMessage` default changed from `false` to `true`**

Failed messages now land on the dead-letter queue wrapped in the `DeadLetterEnvelope` (with exception type, message, stack trace, inner exceptions, `MessageId`, `CorrelationId`, and `reprocessAttempts`) by default. Previously the raw payload was nacked and the broker forwarded it untouched.

**Migration:** if you have downstream tooling that reads the DLQ and expects the original message bytes, either set `c.ExtendDeadletterMessage = false` per consumer to keep the old shape, or update the consumer to deserialize `DeadLetterEnvelope` and read `envelope.MessageData`. Consumers that already set this to `true` (and anyone using the dead-letter reprocessor, which forces it on) are unaffected.

## ⚠️ Breaking Changes (v5.0.0)

**`IRabbitFlowConsumer<TEvent>.HandleAsync` signature changed**

A new `RabbitFlowMessageContext` parameter was added to provide AMQP metadata (MessageId, CorrelationId, headers, delivery info) directly to each consumer.

```diff
 - Task HandleAsync(TEvent message, CancellationToken cancellationToken);
 + Task HandleAsync(TEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken);
```

**How to migrate:** Add the `RabbitFlowMessageContext context` parameter to every `HandleAsync` implementation. If you don't need the context, simply ignore it:

```csharp

public Task HandleAsync(MyEvent message, RabbitFlowMessageContext context, CancellationToken ct)
{
    // context is available but not required to use
    return ProcessAsync(message, ct);
}
```

**Other changes in v5.0.0:**

- `PublishAsync` / `PublishBatchAsync` now accept an optional `correlationId` parameter.

- `PublishAsync` returns `PublishResult` (instead of `bool`). Use `result.Success` instead of the raw return value.
- `PublishBatchAsync` is new — publish multiple messages atomically (`Transactional`) or individually confirmed (`Confirm`).
- `ChannelMode` was removed from `PublisherOptions` — it is now a per-call parameter on `PublishBatchAsync` (defaults to `Transactional`). Single-message publishes always use publisher confirms.
- `PublisherOptions.IdempotencyEnabled` introduced (later removed in v6.0.0; `MessageId` is now always assigned).

---

## License

This project is licensed under the [MIT License](LICENSE.txt).
