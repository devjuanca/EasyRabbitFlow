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

## ⚠️ Breaking Changes (v8.0.0)

**`IRabbitFlowTemporary.RunAsync` now returns `TemporaryRunResult` instead of `int`**

All three overloads return a rich result (`TotalMessages`, `PublishedMessages`, `ProcessedMessages`, `SucceededMessages`, `FailedMessages`, `Success`, `Duration`, `Errors`); the `<T, TResult>` overload returns `TemporaryRunResult<TResult>` with a `Results` collection. See [Temporary Batch Processing](#temporary-batch-processing).

**Migration:** replace `int processed = await temporary.RunAsync(...)` with `var run = await temporary.RunAsync(...)` and read `run.ProcessedMessages` (or the richer counters). The `onCompleted` / `onCompletedAsync` callbacks are unchanged.

**`IRabbitFlowState` has two new interface members**

`GetQueueStateAsync` and `GetQueuesStateAsync` were added (see [Queue State Inspection](#queue-state-inspection)). No change for callers; custom implementations of the interface (e.g. test mocks) must add the new members.

**Transient classification broadened (behavior change)**

The in-process retry policy, the dead-letter envelope, and the reprocessor now share one inheritance-aware classifier: subclasses of `RabbitFlowTransientException`, `TimeoutException`, and transient HTTP failures (`HttpRequestException` with no response or status 408/429/502/503/504) are retried automatically — including through the inner-exception chain. Previously these required manual wrapping; messages that used to go straight to the DLQ may now be retried first. See [Transient Exceptions](#transient-exceptions-and-custom-retry-logic).

**Dead-letter reprocessor parks non-reprocessable messages (behavior change)**

Exhausted, permanent, and malformed messages are moved once to `{queue}-deadletter-parking` (declared durable by the reprocessor) instead of rotating through the DLQ on every cycle. Tooling or operators that inspected those messages in the DLQ should look at the parking queue instead. See [Dead-Letter Reprocessor](#dead-letter-reprocessor).

**Retry policy is now a fixed interval — exponential backoff removed**

`RetryPolicy<TConsumer>` no longer exposes `ExponentialBackoff`, `ExponentialBackoffFactor`, or `MaxRetryDelay`. The in-handler retry is intentionally scoped to short, ephemeral transient failures, so it now always waits a constant `RetryInterval` between attempts. For failures that take minutes to resolve, use the [dead-letter reprocessor](#dead-letter-reprocessor), which retries on a slow recovery cadence without holding the delivery unacknowledged. See [Retry Policies](#retry-policies).

**Migration:** delete `ExponentialBackoff`, `ExponentialBackoffFactor`, and `MaxRetryDelay` from your `ConfigureRetryPolicy(...)` calls (they no longer compile). Keep `MaxRetryCount` and `RetryInterval`. If you relied on long backoff windows, move that workload to the dead-letter reprocessor instead.

**Other changes (non-breaking but notable)**

- Multi-targeting: `netstandard2.1` + `net8.0`.
- OpenTelemetry-ready observability: `ActivitySource` spans with W3C trace-context propagation, a `Meter` with message counters and duration histograms, and a native health check. See [Observability](#observability).
- Temporary runs support a whole-run `RunTimeout` and end early (with a `ConnectionLost` error entry) when the broker connection drops, instead of hanging.
- `onError` in temporary runs now also fires for publish failures, and `TemporaryRunError.MessageIndex` identifies the failed input message.
- The dead-letter envelope captures the original AMQP properties (`DeliveryMode`, headers, `Type`, `AppId`, …) and the reprocessor restores them on replay — persistent messages stay persistent.
- Consumers drain in-flight handlers (bounded) during shutdown before closing channels, avoiding redeliveries after deploys.

---

## ⚠️ Breaking Changes (v7.0.0)

**`ConfigureCustomDeadletter` and `CustomDeadLetterSettings<TConsumer>` removed**

The custom dead-letter feature has been replaced by `AutoGenerateSettings<TConsumer>.DeadLetterReplicas` (see [Dead-Letter Replicas](#dead-letter-replicas)). The new mechanism is strictly more capable:

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

## Table of Contents

- [⚠️ Breaking Changes (v8.0.0)](#️-breaking-changes-v800)
- [⚠️ Breaking Changes (v7.0.0)](#️-breaking-changes-v700)
- [⚠️ Breaking Changes (v6.0.0)](#️-breaking-changes-v600)
- [⚠️ Breaking Changes (v5.0.0)](#️-breaking-changes-v500)
- [Why EasyRabbitFlow?](#why-easyrabbitflow)
- [Architecture Overview](#architecture-overview)
- [Getting Started](#getting-started)
  - [Installation](#installation)
  - [Quick Start](#quick-start)
- [Configuration](#configuration)
  - [Host Settings](#host-settings)
  - [JSON Serialization](#json-serialization)
  - [Publisher Options](#publisher-options)
- [Consumers](#consumers)
  - [Implementing a Consumer](#implementing-a-consumer)
  - [Registering Consumers](#registering-consumers)
  - [Dead-Letter Delivery Guarantee](#dead-letter-delivery-guarantee)
  - [Reserved Name Substrings](#reserved-name-substrings)
  - [Auto-Generate Topology](#auto-generate-topology)
  - [Retry Policies](#retry-policies)
  - [Consumer Timeout](#consumer-timeout)
  - [Dead-Letter Replicas](#dead-letter-replicas)
  - [Dead-Letter Reprocessor](#dead-letter-reprocessor)
  - [Manual DLQ Replay Safety Net](#manual-dlq-replay-safety-net)
- [Publishing Messages](#publishing-messages)
  - [Single Message](#single-message)
  - [Batch Publishing](#batch-publishing)
  - [Idempotency](#idempotency)
  - [Correlation](#correlation)
  - [Per-Call AMQP Options (`PublishOptions`)](#per-call-amqp-options-publishoptions)
- [Message Context](#message-context)
- [Queue State Inspection](#queue-state-inspection)
- [Queue Purging](#queue-purging)
- [Temporary Batch Processing](#temporary-batch-processing)
- [Observability](#observability)
  - [Distributed Tracing](#distributed-tracing)
  - [Metrics](#metrics)
  - [Health Check](#health-check)
- [Transient Exceptions and Custom Retry Logic](#transient-exceptions-and-custom-retry-logic)
- [Sample Project](#sample-project)
- [Full API Reference](#full-api-reference)
- [Performance Notes](#performance-notes)

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

## Architecture Overview

```text
┌─────────────────────────────────────────────────────────────────────┐
│                         Your Application                            │
├────────────┬──────────────┬──────────────┬──────────────────────────┤
│ Publisher  │  Consumers   │   State /    │  Temporary Batch         │
│            │  (Hosted)    │   Purger     │  Processing              │
├────────────┴──────────────┴──────────────┴──────────────────────────┤
│                     EasyRabbitFlow Library                           │
│                                                                     │
│  ┌──────────────┐  ┌────────────────┐  ┌─────────────────────────┐ │
│  │ IRabbitFlow   │  │ ConsumerHosted │  │ IRabbitFlowTemporary    │ │
│  │ Publisher     │  │ Service        │  │ (batch processing)      │ │
│  └──────┬───────┘  └───────┬────────┘  └────────────┬────────────┘ │
│         │                  │                         │              │
│  ┌──────┴──────────────────┴─────────────────────────┴────────┐    │
│  │              RabbitMQ.Client (ConnectionFactory)            │    │
│  └────────────────────────────┬────────────────────────────────┘    │
└───────────────────────────────┼─────────────────────────────────────┘
                                │
                    ┌───────────┴───────────┐
                    │     RabbitMQ Broker    │
                    │                       │
                    │  ┌─────────────────┐  │
                    │  │   Exchanges     │  │
                    │  └────────┬────────┘  │
                    │           │            │
                    │  ┌────────▼────────┐  │
                    │  │     Queues      │  │
                    │  └────────┬────────┘  │
                    │           │            │
                    │  ┌────────▼────────┐  │
                    │  │  Dead-Letter    │  │
                    │  │    Queues       │  │
                    │  └─────────────────┘  │
                    └───────────────────────┘
```

### Message Flow

```text
  Publisher                    RabbitMQ                        Consumer
  ────────                    ────────                        ────────

  PublishAsync() ──────►  Exchange ──routing──► Queue ──────► HandleAsync()
                                                  │               │
                                                  │          (on failure)
                                                  │               │
                                                  │          Retry Policy
                                                  │          (exponential
                                                  │           backoff)
                                                  │               │
                                                  │          (exhausted)
                                                  │               │
                                                  └──► Dead-Letter Queue
```

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

## Configuration

All configuration is done through the `AddRabbitFlow` extension method:

```csharp
builder.Services.AddRabbitFlow(cfg =>
{
    cfg.ConfigureHost(...);                    // Connection settings
    cfg.ConfigureJsonSerializerOptions(...);   // Serialization (optional)
    cfg.ConfigurePublisher(...);               // Publisher behavior (optional)
    cfg.AddConsumer<T>(...);                   // Register consumers
});
```

### Host Settings

```csharp
cfg.ConfigureHost(host =>
{
    host.Host = "rabbitmq.example.com";
    host.Port = 5672;
    host.Username = "admin";
    host.Password = "secret";
    host.VirtualHost = "/";
    host.AutomaticRecoveryEnabled = true;
    host.NetworkRecoveryInterval = TimeSpan.FromSeconds(10);
    host.RequestedHeartbeat = TimeSpan.FromSeconds(30);
});
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Host` | string | `"localhost"` | RabbitMQ server hostname or IP |
| `Port` | int | `5672` | AMQP port |
| `Username` | string | `"guest"` | Authentication username |
| `Password` | string | `"guest"` | Authentication password |
| `VirtualHost` | string | `"/"` | RabbitMQ virtual host |
| `AutomaticRecoveryEnabled` | bool | `true` | Auto-reconnect after failures |
| `TopologyRecoveryEnabled` | bool | `true` | Auto-recover queues/exchanges after reconnect |
| `NetworkRecoveryInterval` | TimeSpan | `10s` | Wait time between recovery attempts |
| `RequestedHeartbeat` | TimeSpan | `30s` | Heartbeat interval for connection health |

### JSON Serialization

Optionally customize how messages are serialized/deserialized:

```csharp
cfg.ConfigureJsonSerializerOptions(json =>
{
    json.PropertyNameCaseInsensitive = true;
    json.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    json.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
```

If not configured, EasyRabbitFlow falls back to `JsonSerializerOptions.Web` — i.e. camelCase property naming with case-insensitive deserialization, the same defaults ASP.NET Core uses for JSON. Override via `ConfigureJsonSerializerOptions` if you need a different policy.

### Publisher Options

```csharp
cfg.ConfigurePublisher(pub =>
{
    pub.DisposePublisherConnection = false; // Keep connection alive (default)
});
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DisposePublisherConnection` | bool | `false` | Dispose connection after each publish |

> Every published message always carries a `MessageId`. By default it is an auto-generated GUID; pass a deterministic key via the `messageId` parameter (single) or `messageIdSelector` (batch) when you need true idempotency from business data — see [Idempotency](#idempotency).

---

## Consumers

### Implementing a Consumer

Every consumer implements `IRabbitFlowConsumer<TEvent>`:

```csharp
public interface IRabbitFlowConsumer<TEvent>
{
    Task HandleAsync(TEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken);
}
```

The `RabbitFlowMessageContext` parameter provides AMQP metadata (MessageId, CorrelationId, headers, etc.) for the message being processed. See [Message Context](#message-context) for details.

Consumers support **full dependency injection** — you can inject any service (scoped, transient, singleton):

```csharp
public class EmailConsumer : IRabbitFlowConsumer<NotificationEvent>
{
    private readonly IEmailService _emailService;
    private readonly ILogger<EmailConsumer> _logger;

    public EmailConsumer(IEmailService emailService, ILogger<EmailConsumer> logger)
    {
        _emailService = emailService;
        _logger = logger;
    }

    public async Task HandleAsync(NotificationEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Sending email to {Recipient}, MessageId={MessageId}", message.Email, context.MessageId);
        await _emailService.SendAsync(message.Email, message.Subject, message.Body, cancellationToken);
    }
}
```

### Registering Consumers

```csharp
cfg.AddConsumer<EmailConsumer>("email-queue", c =>
{
    c.Enable = true;                              // Enable/disable this consumer
    c.PrefetchCount = 5;                          // Messages fetched in parallel
    c.Timeout = TimeSpan.FromSeconds(30);         // Per-message processing timeout
    c.AutoAckOnError = false;                     // Don't ack failed messages
    c.ExtendDeadletterMessage = true;             // Add error details to DLQ messages
    c.ConsumerId = "email-consumer-1";            // Custom connection identifier
});
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enable` | bool | `true` | Whether this consumer is active |
| `QueueName` | string | *(set in constructor)* | Queue to consume from |
| `ConsumerId` | string? | `null` | Custom connection ID (falls back to queue name) |
| `PrefetchCount` | ushort | `1` | How many messages to prefetch |
| `Timeout` | TimeSpan | `30s` | Processing timeout per message |
| `AutoAckOnError` | bool | `false` | Auto-acknowledge on error (message lost) |
| `AutoGenerate` | bool | `false` | Auto-create queue/exchange/DLQ |
| `ExtendDeadletterMessage` | bool | `true` | Enrich dead-letter messages with error details |
| `UnwrapDeadLetterEnvelopes` | bool | `false` | Defensive safety net: detect a `DeadLetterEnvelope` arriving on the main queue (e.g. from a manual DLQ replay) and process the inner payload. See [Manual DLQ Replay Safety Net](#manual-dlq-replay-safety-net). |
| `DisableNameValidation` | bool | `false` | Skip validation of reserved substrings (`deadletter`, `-exchange`, `-routing-key`) against the queue name and any auto-generate names. See [Reserved Name Substrings](#reserved-name-substrings). Only honored when `AutoGenerate = false`. |

### Dead-Letter Delivery Guarantee

When a message exhausts its retries it is guaranteed to leave the main queue and reach the DLQ — it is never silently dropped. The `DeadLetterEnvelope` is published on a **publisher-confirms** channel, so the `await` only completes once the broker has confirmed receipt; if the publish is not confirmed (or serialization fails), the consumer falls back to broker-native `nack` dead-lettering. The dead-letter reprocessor uses confirms too: a re-enqueue or park only acks the message off the DLQ after the broker confirms the new copy, so an unconfirmed publish leaves the message safely on the DLQ.

This closes the message-**loss** window. It does **not** provide exactly-once delivery: a confirmed publish whose subsequent ack fails is redelivered, so a message can reach the DLQ — or be reprocessed — more than once. As with any at-least-once broker, make consumers idempotent; see [Idempotency](#idempotency).

### Reserved Name Substrings

When `AutoGenerate = true`, EasyRabbitFlow derives the dead-letter topology by appending fixed substrings to the user-supplied queue name:

```text
{queueName}-deadletter
{queueName}-deadletter-exchange
{queueName}-deadletter-routing-key
```

To prevent collisions and ambiguous topology, `AddConsumer<T>` validates the supplied `queueName` (and any `ExchangeName` / `RoutingKey` configured via `ConfigureAutoGenerate`) against three reserved substrings — case-insensitive:

- `deadletter`
- `-exchange`
- `-routing-key`

If any name contains one of these, registration throws `RabbitFlowException`:

```csharp
// Throws — "orders-deadletter" collides with the auto-generated DLQ name.
cfg.AddConsumer<OrderConsumer>("orders-deadletter", c => { /* ... */ });
```

```csharp
// Throws — auto-generate exchange/routing-key names follow the same rule.
cfg.AddConsumer<OrderConsumer>("orders", c =>
{
    c.AutoGenerate = true;
    c.ConfigureAutoGenerate(ag =>
    {
        ag.ExchangeName = "orders-exchange"; // throws
    });
});
```

**Opting out (`DisableNameValidation`)**

If you fully own the broker topology — i.e. `AutoGenerate = false` and the framework never derives any dependent names — you can bypass the validation:

```csharp
cfg.AddConsumer<OrderConsumer>("orders-deadletter-archive", c =>
{
    c.AutoGenerate = false;          // required
    c.DisableNameValidation = true;  // explicitly accept the reserved substring
});
```

`DisableNameValidation` is **silently ignored** when `AutoGenerate = true`, because the framework still needs to append the reserved suffixes to derive the DLQ topology and a collision would produce ambiguous queue/exchange names.

### Auto-Generate Topology

When `AutoGenerate = true`, EasyRabbitFlow creates queues, exchanges, and dead-letter queues automatically:

```csharp
cfg.AddConsumer<OrderConsumer>("orders-queue", c =>
{
    c.AutoGenerate = true;
    c.ConfigureAutoGenerate(ag =>
    {
        ag.ExchangeName = "orders-exchange";      // Custom exchange name
        ag.ExchangeType = ExchangeType.Fanout;    // Direct | Fanout | Topic | Headers
        ag.RoutingKey = "orders-routing-key";      // Routing key for binding
        ag.GenerateExchange = true;                // Create the exchange
        ag.GenerateDeadletterQueue = true;         // Create a dead-letter queue
        ag.DurableQueue = true;                    // Queue survives broker restart
        ag.DurableExchange = true;                 // Exchange survives broker restart
        ag.ExclusiveQueue = false;                 // Not limited to one connection
        ag.AutoDeleteQueue = false;                // Don't delete when last consumer disconnects
    });
});
```

**Generated topology when `AutoGenerate = true`:**

```text
                        ┌──────────────────────┐
                        │  orders-exchange      │
                        │  (fanout)             │
                        └──────────┬───────────┘
                                   │ routing-key
                        ┌──────────▼───────────┐
                        │  orders-queue         │──── args: x-dead-letter-exchange
                        │  (durable)            │         x-dead-letter-routing-key
                        └──────────┬───────────┘
                                   │ (on failure)
                  ┌────────────────▼─────────────────┐
                  │  orders-queue-deadletter-exchange │
                  │  (direct)                         │
                  └────────────────┬─────────────────┘
                                   │
                  ┌────────────────▼─────────────────┐
                  │  orders-queue-deadletter          │
                  └──────────────────────────────────┘
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `GenerateExchange` | bool | `true` | Create the exchange |
| `GenerateDeadletterQueue` | bool | `true` | Create dead-letter queue and exchange |
| `ExchangeType` | ExchangeType | `Direct` | `Direct`, `Fanout`, `Topic`, or `Headers` |
| `ExchangeName` | string? | `null` | Custom name (defaults to `{queue}-exchange`) |
| `RoutingKey` | string? | `null` | Custom routing key (defaults to `{queue}-routing-key`) |
| `DurableExchange` | bool | `true` | Exchange survives broker restart |
| `DurableQueue` | bool | `true` | Queue survives broker restart |
| `ExclusiveQueue` | bool | `false` | Queue limited to declaring connection |
| `AutoDeleteQueue` | bool | `false` | Delete queue when last consumer disconnects |
| `Args` | IDictionary? | `null` | Additional RabbitMQ arguments |
| `MaxPriority` | byte? | `null` | Declares the queue as a priority queue with this max priority via `x-max-priority`. Required for the broker to honor messages published with [`PublishOptions.Priority`](#per-call-amqp-options-publishoptions); without it, priority is silently ignored and messages flow FIFO. Keep small (1–10). If `Args` already contains `x-max-priority`, that explicit entry wins. |
| `DeadLetterReplicas` | List&lt;DeadLetterReplica&gt; | empty | Extra queues to bind to the dead-letter exchange. See [Dead-Letter Replicas](#dead-letter-replicas). |

### Retry Policies

Configure how failed messages are retried:

```csharp
c.ConfigureRetryPolicy(r =>
{
    r.MaxRetryCount = 4;               // Number of retries after the initial attempt fails (5 attempts total)
    r.RetryInterval = 1000;            // Fixed delay between retries, in ms
});
```

The in-handler retry policy runs **in-process** while the delivery stays unacknowledged, and it only
retries **transient** failures (see [Transient Exceptions](#transient-exceptions-and-custom-retry-logic)). It uses a **fixed**
interval by design — it is meant for short, ephemeral hiccups (a brief network blip, a momentary 5xx,
a transient deadlock). Keep `RetryInterval` small. For failures that take minutes to resolve, rely on
the [dead-letter reprocessor](#dead-letter-reprocessor) instead, which retries on a slow recovery
cadence without holding the delivery open.

`MaxRetryCount` counts **retries**, not total attempts:

- `0` — no retries (the initial attempt is the only one).
- `1` — one retry after the first failure (up to 2 attempts).
- `4` — four retries after the first failure (up to 5 attempts).

**Example: retry timeline with `MaxRetryCount = 4` and `RetryInterval = 1000`:**

```text
Attempt 1 → fail → wait 1000ms
Attempt 2 → fail → wait 1000ms
Attempt 3 → fail → wait 1000ms
Attempt 4 → fail → wait 1000ms
Attempt 5 → fail → sent to dead-letter queue
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxRetryCount` | int | `0` | Number of retries after the initial attempt fails (`0` = no retries) |
| `RetryInterval` | int | `1000` | Fixed delay between retries (ms) |

### Consumer Timeout

The `Timeout` setting (in `ConsumerSettings`) bounds how long a single handler invocation may run before being cancelled:

```csharp
c.Timeout = TimeSpan.FromSeconds(30); // default
```

Internally it drives a `CancellationTokenSource` passed to `HandleAsync`. On timeout, the token is cancelled, the attempt fails, and the retry policy kicks in.

**Server-side timeout (`x-consumer-timeout`)**

RabbitMQ enforces a delivery acknowledgement timeout: if a message is not acked within the window, the broker closes the channel and redelivers the message. Because the in-process retry policy holds the delivery **unacknowledged** for the whole retry cycle, that cycle must fit inside this window.

- The timeout was introduced in **RabbitMQ 3.8.15** as a **broker-wide** setting (`consumer_timeout`), default 15 minutes — raised to **30 minutes in 3.8.17**.
- Overriding it **per queue** via the `x-consumer-timeout` queue argument requires **RabbitMQ 3.12+**. On older brokers the argument is ignored and only the broker-wide `consumer_timeout` applies — so a long `Timeout` there must be accommodated by raising `consumer_timeout` on the broker instead.

When `AutoGenerate = true`, EasyRabbitFlow sets `x-consumer-timeout` on the declared queue to accommodate the full retry cycle plus a grace period:

```text
x-consumer-timeout = max(60s, Timeout × (MaxRetryCount + 1) + RetryInterval × MaxRetryCount + 30s grace)
```

The value is floored at 60s because RabbitMQ rejects consumer timeouts below one minute. This keeps the broker-side policy in sync with your configured retry behavior, so the broker never kills a consumer mid-retry.

**Channel recovery**

If the broker does close a channel (timeout exceeded, protocol violation, etc.), EasyRabbitFlow automatically recreates the channel, re-applies QoS, and re-subscribes the consumer — with exponential backoff between attempts (1s, 2s, 4s… capped at 30s). Connection-level closures are handled the same way.

> **⚠️ Queue arguments are immutable in RabbitMQ**
>
> Because `x-consumer-timeout` is **derived** from `Timeout`, `MaxRetryCount` and `RetryInterval`, changing any of those settings changes the value the consumer would declare. RabbitMQ does not allow redeclaring an existing queue with different arguments. The same situation arises when upgrading from a version (< 5.1.0) that created the queue without the argument at all.
>
> EasyRabbitFlow does **not** fail the consumer over this. It declares the queue on a throwaway channel, and if RabbitMQ rejects it with `PRECONDITION_FAILED`, the consumer **adopts the existing queue as-is and keeps running**, logging a warning that names the queue. A running consumer with a slightly stale server-side timeout is safer than one that won't start and silently leaves the system idle. The trade-off: a stale `x-consumer-timeout` that is too low for your retry cycle could let the broker close the channel mid-retry. To apply the new arguments:
>
> **Options:**
>
>
> 1. Delete the queue and let EasyRabbitFlow recreate it.
>
>

> 2. Apply a [`consumer-timeout` policy](https://www.rabbitmq.com/docs/consumers#per-queue-delivery-timeout) to the existing queue on the broker and add `x-consumer-timeout` with the same value to `AutoGenerateSettings.Args` so the declare matches.

> 3. Set `AutoGenerate = false` and manage the queue yourself (via policy on the broker).

### Dead-Letter Replicas

Bind additional queues to the auto-generated dead-letter exchange so every dead-lettered message is delivered as a copy to multiple destinations. Useful for audit / observability replicas with their own retention, alerting consumers, or replication into a separate processing pipeline — all without affecting the primary DLQ or the reprocessor.

```csharp
cfg.AddConsumer<OrderConsumer>("orders-queue", c =>
{
    c.AutoGenerate = true;
    c.ExtendDeadletterMessage = true;
    c.ConfigureAutoGenerate(ag =>
    {
        ag.GenerateDeadletterQueue = true;

        ag.DeadLetterReplicas.Add(new DeadLetterReplica
        {
            QueueName = "orders-deadletter-audit",
            Arguments = new Dictionary<string, object?>
            {
                ["x-message-ttl"] = (long)TimeSpan.FromDays(30).TotalMilliseconds   // 30 days
            }
        });

        ag.DeadLetterReplicas.Add(new DeadLetterReplica
        {
            QueueName = "orders-deadletter-alerts"
        });
    });
});
```

**Resulting topology:**

```text
                                          ┌─ orders-queue-deadletter      (primary DLQ — reprocessor reads this one)
                                          │
orders-queue-deadletter-exchange ─────────┼─ orders-deadletter-audit       (30-day audit replica)
   (direct)                               │
                                          └─ orders-deadletter-alerts      (alerting consumer)
```

All bound queues receive an identical copy of every dead-lettered message — RabbitMQ routes them through the same direct exchange and routing key. EasyRabbitFlow declares the replica queues at consumer startup; no extra topology setup is required.

The payload shape is whatever the consumer's `ExtendDeadletterMessage` setting produces — with `true` (the default), each replica receives the full `DeadLetterEnvelope`:

```json
{
  "dateUtc": "2026-01-15T10:30:00Z",
  "messageType": "OrderCreatedEvent",
  "messageId": "9f3...",
  "correlationId": "req-abc-123",
  "messageData": { "orderId": "ORD-123", "total": 99.99 },
  "exceptionType": "TimeoutException",
  "errorMessage": "The operation was canceled.",
  "stackTrace": "...",
  "source": "OrderService",
  "innerExceptions": [],
  "reprocessAttempts": 0
}
```

The shape is the public type `DeadLetterEnvelope` so dead-letter messages can be deserialized with strong typing from any client.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `QueueName` | string | required | Name of the extra queue to declare and bind |
| `Durable` | bool | `true` | Queue survives broker restarts |
| `AutoDelete` | bool | `false` | Delete queue when last consumer disconnects |
| `Arguments` | IDictionary? | `null` | Additional queue arguments (`x-message-ttl`, `x-max-length`, etc.) |

**Notes:**

- The replica queues are bound with the same routing key as the primary DLQ, so they receive **every** dead-lettered message — there is no per-queue filtering.
- Requires `GenerateDeadletterQueue = true`. If `false`, the list is ignored and a warning is logged at startup.
- The [Dead-Letter Reprocessor](#dead-letter-reprocessor) reads only the primary DLQ (`{queue}-deadletter`). Messages re-enqueued by the reprocessor are not replicated a second time — only their original arrival on the DLQ is. Replica queues are independent and never drained by the reprocessor.
- Replica queue names are *not* validated against the [reserved substrings](#reserved-name-substrings) — they are caller-owned destinations, not auto-generated topology.

### Dead-Letter Reprocessor

When in-handler retries are not enough — for example, when the failure is caused by a downstream system that takes hours to recover — you can attach a **dead-letter reprocessor** to a consumer. The reprocessor runs on a fixed cadence, drains the consumer's auto-generated dead-letter queue, and re-publishes each message to the main queue until a maximum attempt count is reached.

```csharp
cfg.AddConsumer<OrderConsumer>("orders-queue", c =>
{
    c.AutoGenerate = true;                       // required
    c.ExtendDeadletterMessage = true;            // forced when reprocessor is enabled

    c.ConfigureRetryPolicy(r => r.MaxRetryCount = 3);

    c.ConfigureDeadLetterReprocess(r =>
    {
        r.Enabled = true;
        r.MaxReprocessAttempts = 5;              // total times the message can be moved DLQ → main
        r.Interval = TimeSpan.FromHours(3);      // run every 3 hours
        // r.MaxMessagesPerCycle = 500;          // optional safety cap; defaults to int.MaxValue (drain entire snapshot)
    });
});
```

**Behavior:**

```text
                          ┌─ DLQ ─┐
                          │       │
                          │ env#1 │  ReprocessAttempts < Max → publish payload to main queue
                          │ env#2 │   (with header x-reprocess-attempts incremented)
                          │ env#3 │
                          └───┬───┘
                              │
          ┌───────────────────┴───────────────────┐
          │                                       │
   Reprocessor cycle                       Main queue picks it up;
   every `Interval`                        if it fails again, the new
                                           envelope is written back
                                           with the bumped counter
                                           preserved via x-reprocess-attempts.
```

If a message exhausts its reprocess budget, the reprocessor moves it to the **parking queue** (`{queue}-deadletter-parking`, declared durable by the reprocessor itself) with `reprocessAttempts` reflecting the final count — so it remains visible in any RabbitMQ client and is not silently dropped, and the DLQ stays free for messages that are still actionable. Permanent and malformed messages are parked the same way, once, instead of rotating through the DLQ on every cycle.

The parking queue is created on demand the first time the reprocessor finds messages in the DLQ to drain (the consumer never declares it, and a cycle that finds the DLQ empty skips it), so it never clutters the broker unless it's actually used. The reprocessor probes it with a passive declare: if the queue already exists it is used **as-is**, so deliberate operator settings (a TTL to age out old failures, a quorum queue type, a `max-length`, …) are respected and the reprocessor never fails with `PRECONDITION_FAILED` fighting over arguments. To apply different arguments, delete the queue and let the reprocessor recreate it with defaults (durable, classic, no extra args).

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | bool | `true` | Whether the reprocessor is active for this consumer |
| `MaxReprocessAttempts` | int | `3` | Maximum times a message is moved from DLQ back to main queue |
| `Interval` | TimeSpan | `3h` | Time between reprocessor runs. **Minimum 10 minutes** (hard floor — use the in-handler `RetryPolicy` for tighter retry cadences) |
| `MaxMessagesPerCycle` | int | `int.MaxValue` | Optional safety cap on messages drained per cycle. By default each cycle drains the whole DLQ snapshot taken at the start of the run; lower this only if you need an explicit ceiling. |

**Constraints:**

- Requires `AutoGenerate = true`. If the consumer manages its own topology, the reprocessor is silently disabled with a warning.
- Forces `ExtendDeadletterMessage = true` so the envelope (including `reprocessAttempts`) is available — a warning is logged if you set it to `false`.
- Operates only on the auto-generated dead-letter queue (`{queue}-deadletter`). Any [Dead-Letter Replicas](#dead-letter-replicas) bound to the same DLX are independent and never drained by the reprocessor.
- **Only transient failures are reprocessed.** A message is eligible only if its envelope's `isTransient` flag is `true` — classified at failure time by the same rules the in-handler `RetryPolicy` uses (see [Transient Exceptions](#transient-exceptions-and-custom-retry-logic)): `RabbitFlowTransientException` and derived types, cancellation/timeout, and transient HTTP failures. Permanent failures (validation, deserialization, business-rule violations) are moved to the parking queue so they remain visible for inspection without churning through the DLQ. Envelopes from older versions (without the flag) fall back to exact type-name matching.
- Counter persistence: the reprocessor sets the AMQP header `x-reprocess-attempts` on every message it re-publishes. The consumer reads this header on receipt and seeds the new envelope with that count if processing fails again, so the counter survives the full cycle DLQ → main → DLQ.
- **AMQP properties are restored on replay.** The envelope captures the original message's `DeliveryMode`, `Type`, `AppId`, `Priority`, `ContentType`, `ReplyTo`, and headers (including `traceparent`) at failure time, and the reprocessor restores them when re-enqueueing — a persistent message stays persistent after a replay. Header values are preserved as strings (binary values are decoded as UTF-8).

### Manual DLQ Replay Safety Net

The recommended way to retry dead-lettered messages is the [Dead-Letter Reprocessor](#dead-letter-reprocessor) — it preserves the original payload and the `reprocessAttempts` counter for you. But operators occasionally replay a message manually (Shovel plugin, management UI, ad-hoc script) by copying it from the DLQ back to the main queue. When that happens, the message body is no longer the original event — it is a `DeadLetterEnvelope` JSON, which deserializes into a default-shaped `TEvent` and silently corrupts the consumer's view of the world.

`UnwrapDeadLetterEnvelopes` is an opt-in defense for that scenario:

```csharp
cfg.AddConsumer<OrderConsumer>("orders-queue", c =>
{
    c.AutoGenerate = true;
    c.ExtendDeadletterMessage = true;
    c.UnwrapDeadLetterEnvelopes = true;  // safety net for manual replays
});
```

**How it works**

For every inbound message, the consumer runs a cheap byte-level fingerprint scan looking for the two most distinctive envelope keys (`"messageData"` and `"exceptionType"`). Only when the fingerprint matches does it parse the body as `DeadLetterEnvelope`, extract the inner `MessageData`, and use it as the actual payload. If the envelope carried a `MessageId` or `CorrelationId`, those are also surfaced via `RabbitFlowMessageContext` when AMQP didn't already provide them.

A warning is logged whenever an unwrap fires, so you can spot manual replays in the logs:

```text
[RABBIT-FLOW]: Detected manually replayed DeadLetterEnvelope on queue orders-queue for OrderConsumer;
unwrapped inner payload. Prefer the DeadLetterReprocessor for replays.
```

**No double-wrapping on re-failure**

If the unwrapped message fails again with `ExtendDeadletterMessage = true`, the new DLQ entry wraps the **original payload**, not the inbound envelope — the dead-letter queue stays at depth 1 instead of accumulating nested envelopes across replays.

**Default is `false`**

The fingerprint check is fast, but it still runs on every message. Leave the flag off unless you actually need the safety net — i.e. unless your operations team manually replays messages from the DLQ. Production deployments that rely exclusively on the reprocessor never need to enable it.

---

## Publishing Messages

Inject `IRabbitFlowPublisher` to publish messages. All single-message publishes use **publisher confirms** — the `await` only completes after the broker confirms receipt.

### Single Message

Returns a `PublishResult` with `Success`, `Destination`, `RoutingKey`, `MessageId`, `TimestampUtc`, and `Error`:

```csharp
public class OrderService
{
    private readonly IRabbitFlowPublisher _publisher;

    public OrderService(IRabbitFlowPublisher publisher)
    {
        _publisher = publisher;
    }

    // Publish directly to a queue
    public async Task CreateOrderAsync(OrderCreatedEvent order)
    {
        var result = await _publisher.PublishAsync(order, queueName: "orders-queue");

        if (!result.Success)
            throw new Exception($"Publish failed: {result.Error?.Message}");
    }

    // Publish to an exchange with routing key and correlation ID
    public async Task BroadcastNotificationAsync(NotificationEvent notification, string correlationId)
    {
        var result = await _publisher.PublishAsync(
            notification,
            exchangeName: "notifications",
            routingKey: "user.created",
            correlationId: correlationId);

        Console.WriteLine($"Published to {result.Destination} at {result.TimestampUtc}");
    }
}
```

**Method signatures:**

```csharp
// Publish to exchange (with routing key) — always uses publisher confirms
Task<PublishResult> PublishAsync<TEvent>(TEvent message, string exchangeName, string routingKey,
    string? messageId = null, string? correlationId = null,
    PublishOptions? options = null,
    CancellationToken cancellationToken = default) where TEvent : class;

// Publish directly to queue — always uses publisher confirms
Task<PublishResult> PublishAsync<TEvent>(TEvent message, string queueName,
    string? messageId = null, string? correlationId = null,
    PublishOptions? options = null,
    CancellationToken cancellationToken = default) where TEvent : class;
```

**`PublishResult` properties:**

| Property | Type | Description |
|----------|------|-------------|
| `Success` | bool | Whether the broker confirmed the message |
| `MessageId` | string | Identifier of the published message — caller-supplied via `messageId` parameter or auto-generated GUID. Always populated. |
| `Destination` | string | Target exchange or queue name |
| `RoutingKey` | string | Routing key used (empty for queue publishes) |
| `TimestampUtc` | DateTime | When the publish was executed |
| `Error` | Exception? | The exception if `Success` is `false` |

### Batch Publishing

Use `PublishBatchAsync` to publish multiple messages in a single operation. The `channelMode` parameter controls atomicity:

- **`Transactional`** (default): All-or-nothing — if any message fails, the entire batch is rolled back.
- **`Confirm`**: Each message is individually confirmed — a mid-batch failure does not roll back previous messages.

```csharp
// Atomic batch (Transactional — default)
var result = await publisher.PublishBatchAsync(
    orders,
    exchangeName: "orders-exchange",
    routingKey: "new-order");

Console.WriteLine($"Batch: {result.MessageCount} messages, success={result.Success}");

// Non-atomic batch (Confirm mode — higher throughput)
var result = await publisher.PublishBatchAsync(
    orders,
    queueName: "orders-queue",
    channelMode: ChannelMode.Confirm);
```

**Method signatures:**

```csharp
// Batch to exchange
Task<BatchPublishResult> PublishBatchAsync<TEvent>(IReadOnlyList<TEvent> messages,
    string exchangeName, string routingKey,
    ChannelMode channelMode = ChannelMode.Transactional,
    Func<TEvent, string>? messageIdSelector = null,
    string? correlationId = null,
    PublishOptions? options = null,
    CancellationToken cancellationToken = default) where TEvent : class;

// Batch to queue
Task<BatchPublishResult> PublishBatchAsync<TEvent>(IReadOnlyList<TEvent> messages,
    string queueName,
    ChannelMode channelMode = ChannelMode.Transactional,
    Func<TEvent, string>? messageIdSelector = null,
    string? correlationId = null,
    PublishOptions? options = null,
    CancellationToken cancellationToken = default) where TEvent : class;
```

**`BatchPublishResult` properties:**

| Property | Type | Description |
|----------|------|-------------|
| `Success` | bool | Whether all messages were published |
| `MessageCount` | int | Number of messages in the batch |
| `MessageIds` | IReadOnlyList\<string\> | Identifiers per message (in input order) — produced by `messageIdSelector` when supplied or auto-generated GUIDs otherwise. Always one entry per published message. |
| `Destination` | string | Target exchange or queue name |
| `RoutingKey` | string | Routing key used |
| `ChannelMode` | ChannelMode | Mode used (`Transactional` or `Confirm`) |
| `TimestampUtc` | DateTime | When the batch was executed |
| `Error` | Exception? | The exception if `Success` is `false` |

### Idempotency

Every published message carries a `MessageId` set on `BasicProperties.MessageId` and returned in `PublishResult.MessageId` (or `BatchPublishResult.MessageIds`). There is no opt-out — identification is foundational, not optional.

**Default — auto-generated unique GUID per call:**

```csharp
var result = await publisher.PublishAsync(order, "orders-queue");
// result.MessageId == "f3a9c1d4..." (GUID, unique per call)
```

This is enough for tracing, logs, and per-attempt identification. It is **not** enough for true idempotency — retrying the same logical publish produces a different GUID each time, so consumers can't deduplicate against it.

**Idempotency-friendly publishing — caller-supplied deterministic key:**

For dedup-friendly behavior, pass a `messageId` derived from your business data. Retries of the same logical event then carry the same `MessageId`, giving consumers a stable key they can use in their own deduplication logic.

```csharp
// Single message — derive the key from business data
var result = await publisher.PublishAsync(
    order,
    "orders-queue",
    messageId: $"order-{order.Id}-created");

// Batch — selector produces a key per event
var batchResult = await publisher.PublishBatchAsync(
    orders,
    "orders-queue",
    messageIdSelector: o => $"order-{o.Id}-created");
```

The selector must return a non-empty string. Returning `null` or an empty string fails the batch (rolled back in `Transactional` mode, partially published in `Confirm` mode).

EasyRabbitFlow intentionally does **not** include a built-in idempotency store. Idempotency is usually a domain concern: preventing a duplicate payment, duplicate order, duplicate email, or duplicate inventory adjustment must be coordinated with the same database, external provider, or business invariant that owns the side effect. The library gives you the stable metadata (`MessageId`, `CorrelationId`, `Redelivered`, `ReprocessAttempts`); your application decides what "already processed" means.

Recommended responsibilities:

| Layer | Responsibility |
|-------|----------------|
| Publisher | Provide a deterministic `messageId` for logical operations that may be retried. |
| EasyRabbitFlow | Preserve `MessageId` on the wire and expose it through `RabbitFlowMessageContext`. |
| Consumer/application | Deduplicate using domain storage, unique constraints, transactions, or provider idempotency keys. |

Common keys:

| Use case | Example key |
|----------|-------------|
| Order created | `order-{OrderId}-created` |
| Payment captured | `payment-{PaymentId}-capture` |
| Email notification | `email-{TemplateId}-{Recipient}-{BusinessId}` |
| Inventory reservation | `inventory-{ReservationId}` |

On the consumer side, deduplicate using `RabbitFlowMessageContext.MessageId`:

```csharp
public async Task HandleAsync(OrderEvent message, RabbitFlowMessageContext context, CancellationToken ct)
{
    var messageId = context.MessageId ?? $"order-{message.OrderId}-created";

    var inserted = await _processedMessages.TryInsertAsync(messageId, ct);

    if (!inserted)
    {
        // Duplicate delivery: RabbitFlow will ACK when the handler returns.
        return; // duplicate — already processed
    }

    await ProcessAsync(message, ct);
}
```

For database-backed consumers, prefer an atomic pattern:

1. Insert `MessageId` into a table with a unique constraint.
2. If the insert fails because the key already exists, return without applying the side effect.
3. Apply the side effect in the same transaction when possible.
4. Commit before the handler returns, so the RabbitMQ ACK only happens after your durable state is safe.

For external providers, forward the same deterministic key when the provider supports idempotency keys:

```csharp
public async Task HandleAsync(PaymentCaptured message, RabbitFlowMessageContext context, CancellationToken ct)
{
    var idempotencyKey = context.MessageId ?? $"payment-{message.PaymentId}-capture";

    await _paymentGateway.CaptureAsync(
        message.PaymentId,
        message.Amount,
        idempotencyKey,
        ct);
}
```

If a message comes from a third-party publisher without `MessageId`, either derive a domain key from the payload (preferred) or treat it as non-idempotent and rely on your business operation to be safe on repeat.

### Correlation

Pass a `correlationId` when publishing to trace related messages end-to-end:

```csharp
// Single message with correlation
await publisher.PublishAsync(order, "orders-queue", correlationId: "req-abc-123");

// Exchange publish with correlation
await publisher.PublishAsync(event, "notifications", routingKey: "new", correlationId: requestId);

// Batch — same correlationId shared across all messages
await publisher.PublishBatchAsync(events, "orders-queue", correlationId: batchId);
```

The `correlationId` is set on `BasicProperties.CorrelationId` and received by consumers via `RabbitFlowMessageContext.CorrelationId`. For distributed tracing across services, see [Observability](#observability) — the W3C trace context travels in AMQP headers automatically.

### Per-Call AMQP Options (`PublishOptions`)

`PublishAsync` and `PublishBatchAsync` accept an optional `PublishOptions` parameter that bundles AMQP metadata applied to the published message(s) plus an optional JSON serializer override. All fields are nullable (`null` = not written to the wire) except `DeliveryMode`, which defaults to `Transient`.

```csharp
await publisher.PublishAsync(
    order,
    "orders-queue",
    messageId: $"order-{order.Id}-created",
    correlationId: requestId,
    options: new PublishOptions
    {
        DeliveryMode = MessageDeliveryMode.Persistent,   // survive broker restart (queue must be durable)
        Type = "OrderCreated",                            // logical event name
        AppId = "checkout-svc",                           // publishing application
        Expiration = TimeSpan.FromMinutes(5),             // per-message TTL
        Priority = 7,                                     // requires MaxPriority on the queue (see below)
        Timestamp = DateTimeOffset.UtcNow,
        ReplyTo = "orders-replies",
        ContentType = "application/json",
        Headers = new Dictionary<string, object?>
        {
            ["tenant"] = "acme",
            ["region"] = "eu"
        },
        JsonOptions = customJsonOptions                   // per-call serializer override
    });
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DeliveryMode` | MessageDeliveryMode | `Transient` | `Persistent` survives broker restart (queue must also be durable). `Transient` is in-memory only. |
| `Type` | string? | `null` | AMQP `type` — typically a logical event name independent of the .NET CLR type. |
| `AppId` | string? | `null` | AMQP `app-id` — identifies the publishing application. |
| `Expiration` | TimeSpan? | `null` | Per-message TTL. Broker discards or dead-letters after this window. Serialized as a millisecond string. |
| `Priority` | byte? | `null` | AMQP `priority` (0–9). **Requires the destination queue to be declared with [`MaxPriority`](#auto-generate-topology)**; without it the broker silently delivers messages FIFO. |
| `Timestamp` | DateTimeOffset? | `null` | AMQP `timestamp` — serialized as Unix seconds. |
| `ReplyTo` | string? | `null` | AMQP `reply-to` — names the queue/exchange a consumer should reply on (RPC-style flows). |
| `ContentType` | string? | `null` | AMQP `content-type`. Not set by default to preserve historical wire format; set to `"application/json"` if your consumers need it. |
| `Headers` | IDictionary? | `null` | AMQP headers table. Use for business metadata or routing on headers exchanges. |
| `JsonOptions` | JsonSerializerOptions? | `null` | Per-call override for the JSON serializer. When `null`, the publisher uses the globally configured serializer. |

Consumers read every field above (except `JsonOptions`, which is publisher-side only) via [`RabbitFlowMessageContext`](#message-context).

> **Priority footgun:** setting `options.Priority` on a queue that was **not** declared with `x-max-priority` is silently ignored by the broker — messages are delivered in FIFO order regardless. For auto-generated queues, configure [`AutoGenerateSettings.MaxPriority`](#auto-generate-topology); for queues declared externally, add `x-max-priority` to the queue arguments yourself.

---

## Message Context

Every consumer receives a `RabbitFlowMessageContext` as a parameter of `HandleAsync`, providing access to AMQP metadata of the message being processed:

```csharp
public class OrderConsumer : IRabbitFlowConsumer<OrderCreatedEvent>
{
    public async Task HandleAsync(OrderCreatedEvent message, RabbitFlowMessageContext context, CancellationToken ct)
    {
        // Idempotency check using MessageId
        if (context.MessageId != null && await _db.ExistsAsync(context.MessageId))
        {
            _logger.LogWarning("Duplicate message {Id}, skipping", context.MessageId);
            return;
        }

        // Trace correlation across services
        _logger.LogInformation("Processing order. CorrelationId={CorrelationId}", context.CorrelationId);

        // Check if this is a redelivery
        if (context.Redelivered)
        {
            _logger.LogWarning("Redelivered message, DeliveryTag={Tag}", context.DeliveryTag);
        }

        // Detect messages re-enqueued by the dead-letter reprocessor
        if (context.ReprocessAttempts > 0)
        {
            _logger.LogInformation("Reprocessing message (attempt {N}) after a previous failure.", context.ReprocessAttempts);
        }

        await ProcessOrderAsync(message, ct);
    }
}
```

**`RabbitFlowMessageContext` properties:**

| Property | Type | Description |
|----------|------|-------------|
| `MessageId` | string? | Identifier from `BasicProperties.MessageId`. Always populated for messages published via `IRabbitFlowPublisher` (caller-supplied or auto-GUID). May be `null` only for messages produced by third-party publishers that don't set it. |
| `CorrelationId` | string? | Correlation ID from `BasicProperties.CorrelationId` (set via `correlationId` parameter) |
| `Exchange` | string? | Exchange that delivered the message (empty for direct queue publishes) |
| `RoutingKey` | string? | Routing key used when the message was published |
| `Headers` | IDictionary? | Custom AMQP headers from `BasicProperties.Headers` |
| `DeliveryTag` | ulong | Broker-assigned delivery tag for this message |
| `Redelivered` | bool | Whether the broker redelivered this message |
| `ReprocessAttempts` | int | Number of times this message was re-enqueued from the DLQ by the [Dead-Letter Reprocessor](#dead-letter-reprocessor). `0` for messages that have never been reprocessed. |
| `DeliveryMode` | MessageDeliveryMode? | AMQP `delivery-mode`. `null` when the publisher did not set it explicitly (the broker treats absent as `Transient`). |
| `Type` | string? | AMQP `type` — typically a logical event name independent of the .NET CLR type. `null` when not set on the wire. |
| `AppId` | string? | AMQP `app-id` — identifies the publishing application. `null` when not set on the wire. |
| `Expiration` | TimeSpan? | AMQP `expiration` decoded from its millisecond string. `null` when not set on the wire. The broker enforces the TTL; this value is informational. |
| `Priority` | byte? | AMQP `priority`. `null` when not set on the wire. Only meaningful on priority queues (see [`MaxPriority`](#auto-generate-topology)). |
| `Timestamp` | DateTimeOffset? | AMQP `timestamp` decoded from AMQP Unix seconds. `null` when not set on the wire. |
| `ReplyTo` | string? | AMQP `reply-to`. `null` when not set on the wire. |
| `ContentType` | string? | AMQP `content-type`. `null` when not set on the wire. |

> **Note:** `RabbitFlowMessageContext` is immutable — all properties are read-only and populated automatically from `BasicDeliverEventArgs` before `HandleAsync` is invoked.

---

## Queue State Inspection

Use `IRabbitFlowState` to query queue metadata at runtime. For health checks and dashboards, prefer `GetQueueStateAsync` — it returns everything in a **single broker round trip** (each individual method opens its own connection):

```csharp
public class HealthCheckService
{
    private readonly IRabbitFlowState _state;

    public HealthCheckService(IRabbitFlowState state)
    {
        _state = state;
    }

    public async Task<QueueState> GetQueueHealthAsync(string queueName)
    {
        // One connection, one round trip: Exists, MessageCount, ConsumerCount, IsEmpty, HasConsumers
        return await _state.GetQueueStateAsync(queueName);
    }

    public async Task<IReadOnlyList<QueueState>> GetAllQueuesHealthAsync(string[] queueNames)
    {
        // One shared connection for the whole batch
        return await _state.GetQueuesStateAsync(queueNames);
    }
}
```

`GetQueueStateAsync` does **not throw** when the queue is missing — it reports `Exists = false` with zero counts, which is what a health endpoint usually wants. The granular methods remain available and still throw on missing queues:

| Method | Returns | Description |
|--------|---------|-------------|
| `GetQueueStateAsync(queueName)` | `Task<QueueState>` | Full snapshot in one round trip: `Exists`, `MessageCount`, `ConsumerCount`, `IsEmpty`, `HasConsumers` |
| `GetQueuesStateAsync(queueNames)` | `Task<IReadOnlyList<QueueState>>` | Snapshots for several queues over a single connection, in input order |
| `IsEmptyQueueAsync(queueName)` | `Task<bool>` | Is the queue empty? |
| `GetQueueLengthAsync(queueName)` | `Task<uint>` | Number of messages in the queue |
| `GetConsumersCountAsync(queueName)` | `Task<uint>` | Number of active consumers |
| `HasConsumersAsync(queueName)` | `Task<bool>` | Does the queue have any consumers? |

---

## Queue Purging

Use `IRabbitFlowPurger` to remove all messages from queues:

```csharp
// Purge a single queue
await purger.PurgeMessagesAsync("orders-queue");

// Purge multiple queues at once
await purger.PurgeMessagesAsync(new[] { "orders-queue", "emails-queue", "notifications-queue" });
```

---

## Temporary Batch Processing

`IRabbitFlowTemporary` is designed for **fire-and-forget batch workflows** — process a collection of messages through RabbitMQ with automatic queue creation and cleanup.

**Ideal for:**

- Background jobs (PDF generation, email sending, report calculation)
- One-time batch processing (database cleanup, data migration)
- Parallel processing with configurable concurrency

### Basic Usage

```csharp
public class InvoiceService
{
    private readonly IRabbitFlowTemporary _temporary;

    public InvoiceService(IRabbitFlowTemporary temporary)
    {
        _temporary = temporary;
    }

    public async Task ProcessInvoiceBatchAsync(List<Invoice> invoices)
    {
        TemporaryRunResult run = await _temporary.RunAsync(
            invoices,
            onMessageReceived: async (invoice, ct) =>
            {
                Console.WriteLine($"Processing invoice {invoice.Id}...");
                await Task.Delay(500, ct); // Simulate work
            },
            onCompleted: (total, errors) =>
            {
                Console.WriteLine($"Done! Processed: {total}, Errors: {errors}");
            },
            options: new RunTemporaryOptions
            {
                PrefetchCount = 10,
                Timeout = TimeSpan.FromSeconds(30),
                CorrelationId = Guid.NewGuid().ToString()
            });

        Console.WriteLine($"Succeeded: {run.SucceededMessages}, Failed: {run.FailedMessages}");
    }
}
```

### Error Handling with `onError`

The `onError` callback is invoked whenever a message fails to publish or fails during processing (timeout, cancellation, or exception). Use it to decide what to do with failed messages — log them, persist them, or republish to another queue:

```csharp
TemporaryRunResult run = await _temporary.RunAsync(
    invoices,
    onMessageReceived: async (invoice, ct) =>
    {
        await ProcessInvoiceAsync(invoice, ct);
    },
    onCompleted: (total, errors) =>
    {
        Console.WriteLine($"Done! Processed: {total}, Errors: {errors}");
    },
    onError: async (failedInvoice, ct) =>
    {
        // Store the failed message for later retry or manual review
        await _failedMessageStore.SaveAsync(failedInvoice, ct);

        // Or republish to a dead-letter queue
        await _publisher.PublishAsync(failedInvoice, "invoices-failed-queue");
    },
    options: new RunTemporaryOptions
    {
        PrefetchCount = 10,
        Timeout = TimeSpan.FromSeconds(30)
    });

if (!run.Success)
{
    Console.WriteLine($"Temporary batch completed with {run.FailedMessages} failures.");
}
```

> **Note:** If `onError` itself throws, the exception is caught and logged internally — it will not break the batch processing flow.

### Async Completion Callback

When the completion logic needs to `await` (e.g. flushing state to a database, publishing a follow-up message, calling another service), use the overload that takes `onCompletedAsync` instead of `onCompleted`. The callback receives the processed count, error count, and the operation's `CancellationToken`:

```csharp
TemporaryRunResult run = await _temporary.RunAsync(
    invoices,
    onMessageReceived: async (invoice, ct) =>
    {
        await ProcessInvoiceAsync(invoice, ct);
    },
    onCompletedAsync: async (total, errors, ct) =>
    {
        await _metrics.RecordBatchAsync(total, errors, ct);
        await _publisher.PublishAsync(new BatchCompleted { Total = total, Errors = errors });
    });
```

Picking which overload to use:

| Use this | When |
|----------|------|
| `onCompleted: (total, errors) => …` | Synchronous wrap-up (logging, in-memory counters) |
| `onCompletedAsync: async (total, errors, ct) => …` | Wrap-up that needs to `await` I/O |

Both overloads coexist — existing callers using `onCompleted` keep working unchanged.

### With Result Collection

```csharp
TemporaryRunResult<InvoiceResult> run = await _temporary.RunAsync<Invoice, InvoiceResult>(
    invoices,
    onMessageReceived: async (invoice, ct) =>
    {
        var result = await ProcessInvoiceAsync(invoice, ct);
        return new InvoiceResult { InvoiceId = invoice.Id, Status = "Completed" };
    },
    onCompletedAsync: async (count, results) =>
    {
        // results is a ConcurrentQueue<InvoiceResult> with all collected results
        Console.WriteLine($"Processed {count} invoices, collected {results.Count} results");
        await SaveResultsAsync(results);
    },
    onError: async (failedInvoice, ct) =>
    {
        await _failedMessageStore.SaveAsync(failedInvoice, ct);
    });

Console.WriteLine($"Collected {run.Results.Count} successful invoice results.");
```

### How It Works

```text
  Your Code                   RabbitMQ (Temporary)            Handler
  ─────────                   ──────────────────              ───────

  RunAsync(messages) ─────►  Create temp queue  ───────────►  onMessageReceived()
       │                     Publish all msgs                      │
       │                           │                          ┌────┴────┐
       │                           │                        success   failure
       │                           │                          │         │
       │                     Consume & process  ◄─────────────┘    onError()
       │                           │
       │                     All processed?
       │                           │ yes
       ◄──────────────────── Delete temp queue
       │                     Call onCompleted()
       │
  return TemporaryRunResult
```

`TemporaryRunResult` exposes `TotalMessages`, `PublishedMessages`, `ProcessedMessages`,
`SucceededMessages`, `FailedMessages`, `Success`, `Duration`, and `Errors`. Each entry in
`Errors` records the run stage where the failure happened (`Publish`, `Deserialize`, `Process`,
`Timeout`, `Cancellation`, `Completion`); publish-stage errors also carry the `MessageIndex`
of the failed input message. The `RunAsync<T, TResult>` overload returns
`TemporaryRunResult<TResult>`, adding a `Results` collection with the values returned by
successful handlers. You can still ignore the returned task result in fire-and-forget flows
and rely on `onCompleted` / `onCompletedAsync` for background bookkeeping.

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `PrefetchCount` | ushort | `1` | Parallel message processing (>0) |
| `Timeout` | TimeSpan? | `null` | Per-message timeout |
| `RunTimeout` | TimeSpan? | `null` | Whole-run timeout: cancels in-progress handlers, reports pending messages as failed, and returns the partial result |
| `QueuePrefixName` | string? | `null` | Custom prefix for the temp queue name |
| `CorrelationId` | string? | `Guid` | Correlation ID for tracing/logging |

The run also ends early — instead of waiting forever — if the underlying connection or channel
is shut down mid-run (broker restart, network failure): in-flight handlers are drained, the
undelivered messages are reported as failed with a `ConnectionLost` error entry, and the
partial result is returned.

---

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
- Messages replayed by the [Dead-Letter Reprocessor](#dead-letter-reprocessor) keep their original `traceparent` (captured in the envelope), so the replay links back to the original trace.
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

A native `IHealthCheck` integrates with the standard health checks pipeline, built on the single-round-trip [queue state inspection](#queue-state-inspection):

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

---

## Transient Exceptions and Custom Retry Logic

EasyRabbitFlow distinguishes between **transient** failures (worth retrying) and **permanent** failures (drop straight to the dead-letter queue). The same set is honored by both the in-handler `RetryPolicy` and the [Dead-Letter Reprocessor](#dead-letter-reprocessor).

### Exceptions auto-recognized as transient

| Exception type | When it fires |
|---|---|
| `RabbitFlowTransientException` **and any derived type** | Explicitly thrown by your consumer to signal a retryable error |
| `OperationCanceledException` / `TaskCanceledException` | Per-attempt timeout (`ConsumerSettings.Timeout`), cooperative cancellations, `HttpClient.Timeout` |
| `TimeoutException` | Classic timeout signal from drivers and clients |
| `HttpRequestException` with **no response** | Connection refused, DNS failure, socket reset — the request never reached a server |
| `HttpRequestException` with status **408, 429, 502, 503, 504** | Rate limiting and upstream/gateway unavailability — waiting helps |

Classification is **inheritance-aware** and also inspects the **inner-exception chain** (up to 10 levels): an `InvalidOperationException` wrapping an `HttpRequestException(429)` is still transient. Anything else is treated as **permanent** and routed to the dead-letter queue without further retry.

> **HTTP rate limits and gateway errors are covered for free.** A `429` or `503` thrown by `HttpClient` (e.g. via `EnsureSuccessStatusCode()`) is recognized automatically — no wrapping needed. A `404` or `400` stays permanent.

### Marking your own errors as transient

If a failure is transient but doesn't fall into the auto-recognized set (a database deadlock, a provider-specific error code…), catch the original exception and rethrow it wrapped in `RabbitFlowTransientException` — or throw your own subclass of it, which is equally recognized:

```csharp
using EasyRabbitFlow.Exceptions;

public class PaymentConsumer : IRabbitFlowConsumer<PaymentEvent>
{
    public async Task HandleAsync(PaymentEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessPaymentAsync(message, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            throw new RabbitFlowTransientException("Payment gateway temporarily unavailable (503).", ex);
        }
        // Any other exception → no retry, sent to dead-letter queue
    }
}
```

**Example — HTTP failures need no wrapping:**

```csharp
public class NotificationConsumer : IRabbitFlowConsumer<NotificationEvent>
{
    private readonly HttpClient _http;

    public NotificationConsumer(HttpClient http) => _http = http;

    public async Task HandleAsync(NotificationEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        var response = await _http.PostAsJsonAsync("https://api.example.com/notify", message, cancellationToken);

        // 429 / 503 / 502 / 504 / 408 → HttpRequestException recognized as transient automatically.
        // Connection refused / DNS failure (no response) → also transient automatically.
        // 400 / 401 / 404 / 500 → permanent, goes to the dead-letter queue.
        response.EnsureSuccessStatusCode();
    }
}
```

**How the reprocessor decides:** when a message is dead-lettered, the failure is classified at capture time and recorded in the envelope as an `isTransient` flag — honoring inheritance and the HTTP rules above. The reprocessor re-enqueues only messages whose flag is `true`. Envelopes written by older library versions (without the flag) fall back to exact exception type-name matching (`RabbitFlowTransientException`, `OperationCanceledException`, `TaskCanceledException`).

### Exception types reference

| Exception | Purpose |
|-----------|---------|
| `RabbitFlowTransientException` | User-facing marker. Throw it from your handler to signal a retryable error to both the in-handler retry policy and the dead-letter reprocessor. |
| `RabbitFlowException` | General library error |
| `RabbitFlowOverRetriesException` | Thrown internally when all retry attempts are exhausted |

---

## Sample Project

`sample/RabbitFlowSample` is a runnable ASP.NET Core API that demonstrates every library feature with self-contained modules under `Samples/` (each has its own README, `.http` file, and topology):

| Module | What it demonstrates |
|--------|----------------------|
| `Notifications/` | Fanout exchange, retry policies, dead-letter envelope + reprocessor, DI lifetimes |
| `Orders/` | Topic exchange with `*` / `#` wildcard bindings |
| `Payments/` | Dead-letter replicas (audit + alerting feeds from the same DLX) |
| `SupportTickets/` | Priority queues (`MaxPriority` + `PublishOptions.Priority`) |
| `Thumbnails/` | Temporary queues: `TemporaryRunResult`, per-job `Timeout`, whole-run `RunTimeout`, fire-and-forget |

It also exposes `GET /health` (the native health check, monitoring queues + consumer presence) and `GET /diagnostics/queues` (unified `QueueState` snapshot of all sample queues).

### Option A — Aspire (recommended): full observability

With Docker (or Podman) running:

```bash
dotnet run --project sample/RabbitFlowSample.AppHost
```

The Aspire AppHost starts a RabbitMQ container (management plugin included), launches the sample API wired to it, and prints the **dashboard URL** in the console. From the dashboard:

- **Resources** — RabbitMQ (with its management UI link) and the sample API (with its Swagger link).
- **Traces** — fire any endpoint from Swagger (e.g. `POST /orders`) and watch the HTTP request span flow into the `publish` span and then into each queue's `process` span, stitched by the W3C trace context traveling in AMQP headers.
- **Metrics** — select the `rabbitflow-sample` resource and browse the `easyrabbitflow.*` instruments (publish/consume counters, processing-duration histogram).

The AppHost injects the RabbitMQ connection string and the OTLP endpoint automatically — no configuration needed.

### Option B — Standalone

Start a broker manually and run the API:

```bash
docker run -d --name rabbitmq -p 5672:5672 -p 15672:15672 rabbitmq:3.13-management
dotnet run --project sample/RabbitFlowSample
```

The sample falls back to `localhost:5672` with `guest`/`guest`, and skips the OTLP exporter when no collector endpoint is configured. Swagger is served at the root URL printed in the console; the RabbitMQ management UI lives at `http://localhost:15672`.

---

## Full API Reference

### Registered Services

| Interface | Lifetime | Description |
|-----------|----------|-------------|
| `IRabbitFlowPublisher` | Singleton | Publish messages to exchanges or queues |
| `IRabbitFlowState` | Singleton | Query queue metadata |
| `IRabbitFlowTemporary` | Singleton | Temporary batch processing |
| `IRabbitFlowPurger` | Singleton | Purge queue messages |
| `ConsumerHostedService` | Hosted | Background consumer lifecycle (via `UseRabbitFlowConsumers`) |

### Extension Methods

```csharp
// Register all EasyRabbitFlow services
IServiceCollection AddRabbitFlow(this IServiceCollection services,
    Action<RabbitFlowConfigurator>? configurator = null);

// Start background consumer processing
IServiceCollection UseRabbitFlowConsumers(this IServiceCollection services);

// Register the native health check (see Observability > Health Check)
IHealthChecksBuilder AddRabbitFlow(this IHealthChecksBuilder builder,
    string name = "rabbitflow",
    Action<RabbitFlowHealthCheckOptions>? configure = null,
    HealthStatus? failureStatus = null,
    IEnumerable<string>? tags = null);
```

### Observability Constants

| Constant | Value | Use |
|----------|-------|-----|
| `RabbitFlowDiagnostics.ActivitySourceName` | `"EasyRabbitFlow"` | `AddSource(...)` for traces |
| `RabbitFlowDiagnostics.MeterName` | `"EasyRabbitFlow"` | `AddMeter(...)` for metrics |

### RabbitFlowConfigurator Methods

| Method | Description |
|--------|-------------|
| `ConfigureHost(Action<HostSettings>)` | Set RabbitMQ connection details |
| `ConfigureJsonSerializerOptions(Action<JsonSerializerOptions>)` | Customize JSON serialization |
| `ConfigurePublisher(Action<PublisherConnectionOptions>?)` | Configure publisher behavior |
| `AddConsumer<TConsumer>(string queueName, Action<ConsumerSettings<TConsumer>>)` | Register a consumer |

---

## Performance Notes

EasyRabbitFlow is designed for high-throughput scenarios:

- **Zero per-message reflection** — consumer handlers are compiled via expression trees at startup, not resolved per message.
- **Connection pooling** — publisher reuses a single connection by default.
- **Prefetch control** — tune `PrefetchCount` for optimal throughput vs. memory usage.
- **Thread-safe channel operations** — all channel I/O (ACK/NACK/Publish) is serialized via per-channel semaphores, preventing race conditions when `PrefetchCount > 1`.
- **Semaphore-based concurrency** — internal semaphores prevent consumer overload.
- **Automatic recovery** — connections auto-recover after network failures with configurable intervals.

**Recommended settings for high throughput:**

```csharp
cfg.AddConsumer<MyConsumer>("high-volume-queue", c =>
{
    c.PrefetchCount = 50;                          // Process 50 messages concurrently
    c.Timeout = TimeSpan.FromSeconds(60);          // Generous timeout for heavy processing
    c.ConfigureRetryPolicy(r =>
    {
        r.MaxRetryCount = 3;
        r.RetryInterval = 500;                     // Fixed, ephemeral delay between retries
    });
});

cfg.ConfigurePublisher(pub =>
{
    pub.DisposePublisherConnection = false;         // Reuse connection
});
```

---

## License

This project is licensed under the [MIT License](LICENSE.txt).
