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
  - [Auto-Generate Topology](#auto-generate-topology)
  - [Retry Policies](#retry-policies)
  - [Consumer Timeout](#consumer-timeout)
  - [Custom Dead-Letter Queues](#custom-dead-letter-queues)
  - [Dead-Letter Reprocessor](#dead-letter-reprocessor)
- [Publishing Messages](#publishing-messages)
  - [Single Message](#single-message)
  - [Batch Publishing](#batch-publishing)
  - [Idempotency](#idempotency)
  - [Correlation](#correlation)
- [Message Context](#message-context)
- [Queue State Inspection](#queue-state-inspection)
- [Queue Purging](#queue-purging)
- [Temporary Batch Processing](#temporary-batch-processing)
- [Transient Exceptions and Custom Retry Logic](#transient-exceptions-and-custom-retry-logic)
- [Full API Reference](#full-api-reference)
- [Performance Notes](#performance-notes)

---

## Why EasyRabbitFlow?

| Feature | EasyRabbitFlow |
|---------|----------------|
| Fluent, strongly-typed configuration | ✅ |
| Automatic queue / exchange / dead-letter generation | ✅ |
| Reflection-free per-message processing | ✅ |
| Configurable retry with exponential backoff | ✅ |
| Temporary batch processing with auto-cleanup | ✅ |
| Queue state & purge utilities | ✅ |
| Full DI integration (scoped/transient/singleton) | ✅ |
| Publisher confirms (single) & transactional batch | ✅ |
| Built-in `MessageId` on every message (auto-GUID or caller-supplied for true idempotency) | ✅ |
| CorrelationId support (end-to-end tracing) | ✅ |
| `RabbitFlowMessageContext` per-message metadata | ✅ |
| Rich `PublishResult` / `BatchPublishResult` types | ✅ |
| Thread-safe channel operations | ✅ |
| .NET Standard 2.1 (works with .NET 6, 7, 8, 9+) | ✅ |

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
                r.ExponentialBackoff = true;
                r.ExponentialBackoffFactor = 2;
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

If not configured, the default `JsonSerializerOptions` are used.

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

### Retry Policies

Configure how failed messages are retried:

```csharp
c.ConfigureRetryPolicy(r =>
{
    r.MaxRetryCount = 4;               // Number of retries after the initial attempt fails (5 attempts total)
    r.RetryInterval = 1000;            // Base delay in ms
    r.ExponentialBackoff = true;        // Enable exponential backoff
    r.ExponentialBackoffFactor = 2;     // Multiply delay by this factor
    r.MaxRetryDelay = 30_000;           // Cap delay at 30 seconds
});
```

`MaxRetryCount` counts **retries**, not total attempts:

- `0` — no retries (the initial attempt is the only one).
- `1` — one retry after the first failure (up to 2 attempts).
- `4` — four retries after the first failure (up to 5 attempts).

**Example: retry timeline with `MaxRetryCount = 4` and exponential backoff (factor=2, base=1000ms):**

```text
Attempt 1 → fail → wait 1000ms
Attempt 2 → fail → wait 2000ms
Attempt 3 → fail → wait 4000ms
Attempt 4 → fail → wait 8000ms
Attempt 5 → fail → sent to dead-letter queue
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxRetryCount` | int | `0` | Number of retries after the initial attempt fails (`0` = no retries) |
| `RetryInterval` | int | `1000` | Base delay between retries (ms) |
| `ExponentialBackoff` | bool | `false` | Enable exponential backoff |
| `ExponentialBackoffFactor` | int | `1` | Multiplier for exponential growth |
| `MaxRetryDelay` | int | `60000` | Upper bound for delay (ms) |

### Consumer Timeout

The `Timeout` setting (in `ConsumerSettings`) bounds how long a single handler invocation may run before being cancelled:

```csharp
c.Timeout = TimeSpan.FromSeconds(30); // default
```

Internally it drives a `CancellationTokenSource` passed to `HandleAsync`. On timeout, the token is cancelled, the attempt fails, and the retry policy kicks in.

**Server-side timeout (`x-consumer-timeout`)**

RabbitMQ also enforces its own per-queue delivery acknowledgement timeout (default 30 minutes). If a message is not acked within that window, the broker closes the channel with `PRECONDITION_FAILED` and requeues the message.

When `AutoGenerate = true`, EasyRabbitFlow automatically sets `x-consumer-timeout` on the declared queue to accommodate the full retry cycle plus a grace period:

```text
x-consumer-timeout = Timeout × (MaxRetryCount + 1) + Σ RetryIntervals + 30s grace
```

This keeps the broker-side policy in sync with your configured retry behavior, so the broker never kills a consumer mid-retry.

**Channel recovery**

If the broker does close a channel (timeout exceeded, protocol violation, etc.), EasyRabbitFlow automatically recreates the channel, re-applies QoS, and re-subscribes the consumer — with exponential backoff between attempts (1s, 2s, 4s… capped at 30s). Connection-level closures are handled the same way.

> **⚠️ Migration note (upgrading from < 5.1.0)**
>
> Queue arguments are immutable in RabbitMQ. If you had a queue created by a previous version (without `x-consumer-timeout`), the next startup with `AutoGenerate = true` will fail with `PRECONDITION_FAILED: inequivalent arg 'x-consumer-timeout'`.
>
> **Options:**
>
>
> 1. Delete the queue and let EasyRabbitFlow recreate it.
>
>

> 2. Apply a [`consumer-timeout` policy](https://www.rabbitmq.com/docs/consumers#per-queue-delivery-timeout) to the existing queue on the broker and add `x-consumer-timeout` with the same value to `AutoGenerateSettings.Args` so the declare matches.

> 3. Set `AutoGenerate = false` and manage the queue yourself (via policy on the broker).

### Custom Dead-Letter Queues

Route failed messages to a specific dead-letter queue:

```csharp
c.ConfigureCustomDeadletter(dl =>
{
    dl.DeadletterQueueName = "custom-errors-queue";
});
```

When `ExtendDeadletterMessage = true`, the dead-letter message includes full error details:

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

The shape is exposed as the public type `DeadLetterEnvelope` so dead-letter messages can be deserialized with strong typing from any client.

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

If a message exhausts its reprocess budget, the reprocessor re-publishes it back to the dead-letter queue (with `reprocessAttempts` reflecting the final count) so it remains visible in any RabbitMQ client and is not silently dropped.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | bool | `true` | Whether the reprocessor is active for this consumer |
| `MaxReprocessAttempts` | int | `3` | Maximum times a message is moved from DLQ back to main queue |
| `Interval` | TimeSpan | `3h` | Time between reprocessor runs. **Minimum 10 minutes** (hard floor — use the in-handler `RetryPolicy` for tighter retry cadences) |
| `MaxMessagesPerCycle` | int | `int.MaxValue` | Optional safety cap on messages drained per cycle. By default each cycle drains the whole DLQ snapshot taken at the start of the run; lower this only if you need an explicit ceiling. |

**Constraints:**

- Requires `AutoGenerate = true`. If the consumer manages its own topology, the reprocessor is silently disabled with a warning.
- Forces `ExtendDeadletterMessage = true` so the envelope (including `reprocessAttempts`) is available — a warning is logged if you set it to `false`.
- Operates only on the auto-generated dead-letter queue (`{queue}-deadletter`). Messages routed via `ConfigureCustomDeadletter` are not processed.
- **Only transient failures are reprocessed.** A message is eligible only if its `exceptionType` in the envelope is `OperationCanceledException`, `TaskCanceledException`, or `RabbitFlowTransientException` — the same set the in-handler `RetryPolicy` retries. Permanent failures (validation, deserialization, business-rule violations) stay in the DLQ untouched so they remain visible for inspection.
- Counter persistence: the reprocessor sets the AMQP header `x-reprocess-attempts` on every message it re-publishes. The consumer reads this header on receipt and seeds the new envelope with that count if processing fails again, so the counter survives the full cycle DLQ → main → DLQ.

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
    string? correlationId = null, string publisherId = "",
    JsonSerializerOptions? jsonOptions = null,
    CancellationToken cancellationToken = default) where TEvent : class;

// Publish directly to queue — always uses publisher confirms
Task<PublishResult> PublishAsync<TEvent>(TEvent message, string queueName,
    string? correlationId = null, string publisherId = "",
    JsonSerializerOptions? jsonOptions = null,
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
    string? correlationId = null, string publisherId = "",
    JsonSerializerOptions? jsonOptions = null,
    CancellationToken cancellationToken = default) where TEvent : class;

// Batch to queue
Task<BatchPublishResult> PublishBatchAsync<TEvent>(IReadOnlyList<TEvent> messages,
    string queueName,
    ChannelMode channelMode = ChannelMode.Transactional,
    string? correlationId = null, string publisherId = "",
    JsonSerializerOptions? jsonOptions = null,
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

**True idempotency — caller-supplied deterministic key:**

For dedup-friendly behavior, pass a `messageId` derived from your business data. Retries of the same logical event then carry the same `MessageId` and consumers can short-circuit duplicates.

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

On the consumer side, deduplicate using `RabbitFlowMessageContext.MessageId`:

```csharp
public async Task HandleAsync(OrderEvent message, RabbitFlowMessageContext context, CancellationToken ct)
{
    if (context.MessageId != null && await _seen.ContainsAsync(context.MessageId, ct))
        return; // duplicate — already processed
    await ProcessAsync(message, ct);
    await _seen.AddAsync(context.MessageId!, ct);
}
```

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

The `correlationId` is set on `BasicProperties.CorrelationId` and received by consumers via `RabbitFlowMessageContext.CorrelationId`.

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

> **Note:** `RabbitFlowMessageContext` is immutable — all properties are read-only and populated automatically from `BasicDeliverEventArgs` before `HandleAsync` is invoked.

---

## Queue State Inspection

Use `IRabbitFlowState` to query queue metadata at runtime:

```csharp
public class HealthCheckService
{
    private readonly IRabbitFlowState _state;

    public HealthCheckService(IRabbitFlowState state)
    {
        _state = state;
    }

    public async Task<object> GetQueueHealthAsync(string queueName)
    {
        return new
        {
            IsEmpty = await _state.IsEmptyQueueAsync(queueName),
            MessageCount = await _state.GetQueueLengthAsync(queueName),
            ConsumerCount = await _state.GetConsumersCountAsync(queueName),
            HasConsumers = await _state.HasConsumersAsync(queueName)
        };
    }
}
```

| Method | Returns | Description |
|--------|---------|-------------|
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
        int processed = await _temporary.RunAsync(
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
    }
}
```

### Error Handling with `onError`

The `onError` callback is invoked whenever a message fails during processing (timeout, cancellation, or exception). Use it to decide what to do with failed messages — log them, persist them, or republish to another queue:

```csharp
int processed = await _temporary.RunAsync(
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
```

> **Note:** If `onError` itself throws, the exception is caught and logged internally — it will not break the batch processing flow.

### With Result Collection

```csharp
int processed = await _temporary.RunAsync<Invoice, InvoiceResult>(
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
  return count
```

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `PrefetchCount` | ushort | `1` | Parallel message processing (>0) |
| `Timeout` | TimeSpan? | `null` | Per-message timeout |
| `QueuePrefixName` | string? | `null` | Custom prefix for the temp queue name |
| `CorrelationId` | string? | `Guid` | Correlation ID for tracing/logging |

---

## Transient Exceptions and Custom Retry Logic

EasyRabbitFlow distinguishes between **transient** failures (worth retrying) and **permanent** failures (drop straight to the dead-letter queue). The same set is honored by both the in-handler `RetryPolicy` and the [Dead-Letter Reprocessor](#dead-letter-reprocessor).

### Exceptions auto-recognized as transient

| Exception type | When it fires |
|---|---|
| `OperationCanceledException` | Per-attempt timeout (configured via `ConsumerSettings.Timeout`) |
| `TaskCanceledException` | Same as above, plus most cooperative cancellations — including `HttpClient.Timeout` |
| `RabbitFlowTransientException` | Explicitly thrown by your consumer to signal a retryable error |

Anything else thrown by your handler is treated as **permanent** and routed to the dead-letter queue without further retry.

> **HttpClient timeouts are covered for free.** When `HttpClient.SendAsync` times out it throws `TaskCanceledException`, which is already in the transient set — no wrapping needed.

### Marking your own errors as transient

If a failure is transient but doesn't fall into the auto-recognized set (a `429 Too Many Requests`, a `503`, a database deadlock…), catch the original exception and rethrow it wrapped in `RabbitFlowTransientException`:

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

**Example — making `429 Too Many Requests` transient:**

A rate-limited downstream API responds with `429`. The failure is temporary by definition (waiting helps), so we want it retried by both the in-handler policy and, if the message ends up dead-lettered, by the reprocessor:

```csharp
using EasyRabbitFlow.Exceptions;

public class NotificationConsumer : IRabbitFlowConsumer<NotificationEvent>
{
    private readonly HttpClient _http;

    public NotificationConsumer(HttpClient http) => _http = http;

    public async Task HandleAsync(NotificationEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("https://api.example.com/notify", message, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // Connection refused, DNS failure, socket reset… all worth retrying.
            throw new RabbitFlowTransientException("Notification API unreachable.", ex);
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // Mark 429 explicitly as transient so retry / reprocessor pick it up.
            throw new RabbitFlowTransientException("Notification API rate-limited (429).");
        }

        response.EnsureSuccessStatusCode(); // 4xx (other than 429) bubbles up as permanent → DLQ
    }
}
```

**Why this matters for the reprocessor:** the reprocessor only re-enqueues messages whose recorded `exceptionType` is one of the three transient types listed above. Wrapping the right errors at the handler level is what makes them eligible for the slow recovery path provided by [`ConfigureDeadLetterReprocess`](#dead-letter-reprocessor).

### Exception types reference

| Exception | Purpose |
|-----------|---------|
| `RabbitFlowTransientException` | User-facing marker. Throw it from your handler to signal a retryable error to both the in-handler retry policy and the dead-letter reprocessor. |
| `RabbitFlowException` | General library error |
| `RabbitFlowOverRetriesException` | Thrown internally when all retry attempts are exhausted |

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
```

### RabbitFlowConfigurator Methods

| Method | Description |
|--------|-------------|
| `ConfigureHost(Action<HostSettings>)` | Set RabbitMQ connection details |
| `ConfigureJsonSerializerOptions(Action<JsonSerializerOptions>)` | Customize JSON serialization |
| `ConfigurePublisher(Action<PublisherOptions>?)` | Configure publisher behavior |
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
        r.RetryInterval = 500;
        r.ExponentialBackoff = true;
        r.ExponentialBackoffFactor = 2;
        r.MaxRetryDelay = 10_000;                  // Cap at 10 seconds
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
