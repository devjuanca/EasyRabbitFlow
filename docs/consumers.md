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
| `PrefetchCount` | ushort | `1` | How many messages to prefetch. Also the **in-process concurrency limit** — at most this many handlers run at once. Must be ≥ 1: a value of `0` (AMQP "unlimited") leaves the consumer with zero processing slots, so it never handles anything. |
| `Timeout` | TimeSpan | `30s` | Handler timeout **per attempt** (each in-process retry gets its own); see [Choosing timeouts](#consumer-timeout). Also arms the [stuck-handler watchdog](#consumer-timeout); a non-positive value (e.g. `Timeout.InfiniteTimeSpan`) disables both. |
| `MaxBrokerDeliveries` | int? | `5` | Give up on a message whose returns the **broker** has already counted this many times (`x-delivery-count`, **quorum queues only**): the handler is not invoked and the message is dead-lettered with its envelope as a permanent failure (`RabbitFlowPoisonMessageException`); the reprocessor **always parks it**, even with `FinalAction = Discard`. Stops a message that keeps coming back from blocking the head of the queue until the broker's `delivery-limit` (20) drops it raw. **What counts depends on the broker:** on RabbitMQ 4.0–4.2 every return (consumer timeout, closed channel or connection, requeue); from **4.3** only failed deliveries (crash, dropped connection) — consumer timeouts, cluster partitions and requeues raise `x-acquired-count` instead, which the guard does not read, so on 4.3+ a message that keeps exceeding the broker's consumer timeout is not caught here (the [stuck-handler watchdog](#consumer-timeout) dead-letters it first, unless `Timeout <= 0`). Applies only to deliveries flagged `redelivered` (a count on a first delivery — publisher-set or left by a manual replay — is ignored), only when the auto-generated dead-letter queue exists, and never with `AutoAckOnError` (that would acknowledge the message unprocessed). No effect on classic queues (no count there). `null` disables. See [Queue types](#queue-types). |
| `AutoAckOnError` | bool | `false` | Auto-acknowledge on error — the message is **lost**, it never reaches the dead-letter queue |
| `AutoGenerate` | bool | `false` | Auto-create queue/exchange/DLQ |
| `ExtendDeadletterMessage` | bool | `true` | Enrich dead-letter messages with error details. Only effective when the DLQ is auto-generated (`AutoGenerate = true` and `GenerateDeadletterQueue = true`); otherwise failures are nacked without an envelope. |
| `UnwrapDeadLetterEnvelopes` | bool | `false` | Defensive safety net: detect a `DeadLetterEnvelope` arriving on the main queue (e.g. from a manual DLQ replay) and process the inner payload. See [Manual DLQ Replay Safety Net](dead-letter.md#manual-dlq-replay-safety-net). |
| `DisableNameValidation` | bool | `false` | Skip validation of reserved substrings (`deadletter`, `-exchange`, `-routing-key` in the queue name; `deadletter` in any auto-generate `ExchangeName` / `RoutingKey`). See [Reserved Name Substrings](#reserved-name-substrings). Only honored when `AutoGenerate = false`. |

### Reserved Name Substrings

When `AutoGenerate = true`, EasyRabbitFlow derives the dead-letter topology by appending fixed substrings to the user-supplied queue name:

```text
{queueName}-deadletter
{queueName}-deadletter-exchange
{queueName}-deadletter-routing-key
```

To prevent collisions and ambiguous topology, `AddConsumer<T>` validates the supplied names against reserved substrings — case-insensitive:

| Name | Reserved substrings |
|------|---------------------|
| `queueName` | `deadletter`, `-exchange`, `-routing-key` |
| `ExchangeName` / `RoutingKey` (via `ConfigureAutoGenerate`) | `deadletter` only |

The queue name is the base every generated name is derived from, so all three suffixes are reserved there. The generated dead-letter names derive only from the queue name and always contain `deadletter`, so an exchange name or routing key can only collide with them through that substring: `-exchange` / `-routing-key` are allowed there. This lets a consumer bind its auto-generated queue to an existing exchange that follows the conventional `-exchange` suffix (e.g. `snapshots-exchange`), and it is the same rule [`DeclareExchange`](configuration.md#application-owned-exchanges) applies.

If a name contains a reserved substring, registration throws `RabbitFlowException`:

```csharp
// Throws — "orders-deadletter" collides with the auto-generated DLQ name.
cfg.AddConsumer<OrderConsumer>("orders-deadletter", c => { /* ... */ });
```

```csharp
cfg.AddConsumer<OrderConsumer>("orders", c =>
{
    c.AutoGenerate = true;
    c.ConfigureAutoGenerate(ag =>
    {
        ag.ExchangeName = "snapshots-exchange";   // OK — "-exchange" is allowed here
        ag.RoutingKey = "orders-routing-key";     // OK — "-routing-key" is allowed here
        // ag.ExchangeName = "orders-deadletter"; // throws — "deadletter" is reserved everywhere
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

When `AutoGenerate = true`, EasyRabbitFlow creates queues, exchanges, and dead-letter queues automatically.
(For exchanges owned by a publisher-only service — no consumer registered — use
[`DeclareExchange`](configuration.md#application-owned-exchanges) instead.)

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
| `DurableQueue` | bool | `true` | Queue survives broker restart. **Keep `true` on RabbitMQ 4.x:** the broker denies non-durable, non-exclusive queues by default (`transient_nonexcl_queues` deprecated), so `DurableQueue = false` on a non-exclusive consumer queue is rejected. See [RabbitMQ 4.x compatibility](../README.md#rabbitmq-4x-compatibility). |
| `ExclusiveQueue` | bool | `false` | Queue limited to declaring connection |
| `AutoDeleteQueue` | bool | `false` | Delete queue when last consumer disconnects |
| `Args` | IDictionary? | `null` | Additional RabbitMQ arguments |
| `QueueType` | QueueType? | `null` | Type of the main queue (`Classic` / `Quorum`), sent as `x-queue-type` (v8.4.0). `null` sends nothing and the **broker's** `default_queue_type` decides — which is how a cluster switched to `default_queue_type = quorum` silently turns every auto-generated queue into a quorum queue. Set it explicitly on business queues. `Quorum` requires `DurableQueue = true` and `ExclusiveQueue = false` (validated at registration); an existing queue of another type is adopted as-is with a warning, never converted. Must agree with `Args["x-queue-type"]` if both are set. See [Queue types](#queue-types). |
| `DeadLetterQueueType` | QueueType? | `null` | Type of the auto-generated dead-letter queue **and** of the reprocessor's parking queue. `null` = same as `QueueType` (or the broker default when that is `null` too). |
| `MaxPriority` | byte? | `null` | Declares the queue as a priority queue with this max priority via `x-max-priority`. Required for the broker to honor messages published with [`PublishOptions.Priority`](publishing.md#per-call-amqp-options-publishoptions); without it, priority is silently ignored and messages flow FIFO. Keep small (1–10). If `Args` already contains `x-max-priority`, that explicit entry wins. |
| `DeadLetterReplicas` | List&lt;DeadLetterReplica&gt; | empty | Extra queues to bind to the dead-letter exchange. See [Dead-Letter Replicas](dead-letter.md#dead-letter-replicas). |

### Retry Policies

> **Deprecated in v8.4.0.** `ConfigureRetryPolicy` is marked `[Obsolete]` and will be removed in the next major
> version. It still works exactly as described below, but prefer the
> [dead-letter reprocessor](dead-letter.md#dead-letter-reprocessor): it retries from the DLQ with a persistent
> counter (`x-reprocess-attempts`), on its own cadence, and never holds a delivery unacknowledged — so it does not
> stretch the broker's [consumer timeout](#consumer-timeout) either. In-process retries are what the
> `x-consumer-timeout` formula accounts for; without them it is simply `max(60s, Timeout + 30s + 10s)`.

Configure how failed messages are retried in-process:

```csharp
c.ConfigureRetryPolicy(r =>
{
    r.MaxRetryCount = 4;               // Number of retries after the initial attempt fails (5 attempts total)
    r.RetryInterval = 1000;            // Fixed delay between retries, in ms
});
```

The in-handler retry policy runs **in-process** while the delivery stays unacknowledged, and it only
retries **transient** failures (see [Transient Exceptions](transient-exceptions.md#transient-exceptions-and-custom-retry-logic)). It uses a **fixed**
interval by design — it is meant for short, ephemeral hiccups (a brief network blip, a momentary 5xx,
a transient deadlock). Keep `RetryInterval` small. For failures that take minutes to resolve, rely on
the [dead-letter reprocessor](dead-letter.md#dead-letter-reprocessor) instead, which retries on a slow recovery
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
| `MaxRetryCount` | int | `0` | Number of retries after the initial attempt fails (`0` = no retries; negative values are normalized to `0`) |
| `RetryInterval` | int | `1000` | Fixed delay between retries (ms) |

Note that only **transient** failures are retried. Non-transient exceptions — including a payload that fails
JSON deserialization, the most common permanent failure in production — go straight to the dead-letter queue
with zero retry attempts. See [Transient Exceptions](transient-exceptions.md#transient-exceptions-and-custom-retry-logic).

### Queue types

RabbitMQ fixes a queue's type (`classic` or `quorum`) when the queue is created, from the `x-queue-type` argument of the declare — or, when the declare carries none, from the broker's `default_queue_type` (per node in `rabbitmq.conf`, or per vhost). Until v8.4.0 EasyRabbitFlow never sent `x-queue-type`, so the type of every auto-generated queue was whatever the broker defaulted to: fine on a classic-default broker, but a cluster configured with `default_queue_type = quorum` silently turns every new queue (main, dead-letter, parking) into a quorum queue, with different semantics (no exclusive/non-durable queues, consumer timeouts enforced by the queue on 4.3+, a delivery limit of 20 on 4.x).

Since v8.4.0 the type can be part of the consumer's configuration:

```csharp
c.ConfigureAutoGenerate(ag =>
{
    ag.QueueType = QueueType.Quorum;             // main queue → x-queue-type = quorum
    ag.DeadLetterQueueType = QueueType.Classic;  // optional: DLQ + parking queue; defaults to QueueType
});
```

- **`null` (default) keeps the old behavior**: nothing is sent, the broker decides. Existing applications are unaffected by the upgrade.
- **Set it explicitly on business queues**, even to `Classic`: the declare then states the type, so a broker whose default changes can no longer change the queue type under the consumer, and a mismatch with an existing queue surfaces as the usual `already exists with arguments that differ` warning (the consumer [adopts the existing queue](#channel--connection-recovery) and keeps running).
- **`Quorum` is validated at registration**: it requires `DurableQueue = true`, `ExclusiveQueue = false` and `AutoDeleteQueue = false` (quorum queues are replicated, always durable and never auto-delete); `AddConsumer` throws a `RabbitFlowException` otherwise, instead of the broker rejecting the declare at startup. The same checks apply when the type comes only from `Args["x-queue-type"]`, and when `DeadLetterQueueType = Quorum` while a dead-letter queue is generated. If `QueueType` and `Args["x-queue-type"]` are both set, they must agree.
- **No queue is ever converted.** A classic queue declared with `QueueType = Quorum` (or the reverse) stays what it is and is adopted with a warning. To move a queue to another type, create it under a new name (or in a new vhost / cluster — blue/green) and switch producers and consumers together; `rabbitmqadmin` v2 can automate the migration of definitions. Note that queues created before v8.4.0 carry no `x-queue-type`; declaring them with `QueueType = Classic` on a classic-default broker is accepted as equivalent.
- **Quorum specifics worth knowing**: every message is written to disk regardless of `DeliveryMode`; on RabbitMQ 4.x the default `delivery-limit` is 20 — a message redelivered more than that (crash loops, not the library's own retries, which never requeue; from 4.3 the limit counts only failed deliveries, not consumer timeouts or requeues) is dead-lettered to the DLQ **raw**, without the library's envelope, and the reprocessor parks it as malformed; on 4.3+ the queue enforces the [consumer timeout](#consumer-timeout) by cancelling the consumer, which the library handles by re-subscribing.

### Consumer Timeout

**Choosing timeouts**

Every consumer has two timeouts: `Timeout`, enforced by the library on each handler **attempt**, and the broker's delivery acknowledgement timeout (`x-consumer-timeout`), enforced by RabbitMQ on the whole **delivery**. You configure the first; the library derives the second from it (formula below). In practice:

1. **Set `Timeout` to the realistic worst case of one attempt** — the handler's slowest normal run plus a margin — not of the whole delivery. Each in-process retry gets its own `Timeout`, and the derived broker timeout already covers all of them.
2. **Pass the `CancellationToken` to every I/O call in the handler.** A handler that honours it fails as a transient timeout (retried, and eligible for the reprocessor); one that ignores it is abandoned by the stuck-handler watchdog and dead-lettered as a permanent failure.
3. **Leave `x-consumer-timeout` to the library.** A value in `AutoGenerateSettings.Args` replaces the derived one, and a `consumer-timeout` policy on the broker can also apply to the queue. If you set either, keep it at or above the value of the formula below; otherwise the broker takes the delivery back in the middle of the retry cycle.
4. **With `AutoGenerate = false`, set it yourself.** The library declares nothing, so give the queue an `x-consumer-timeout` at or above the formula value (accepted on quorum queues; RabbitMQ 4.x rejects it on classic queues) or cover it with a `consumer-timeout` policy.
5. **Avoid `Timeout <= 0`.** It disables the handler timeout *and* the watchdog, and no `x-consumer-timeout` is sent, so only the broker-wide setting bounds a hung handler: 30 minutes by default on every queue type up to RabbitMQ 4.2, the broker-wide quorum default on 4.3+, and nothing at all on classic queues from 4.3. On 4.3+ a message that always outlives the broker timeout is not caught by `MaxBrokerDeliveries` either (consumer timeouts no longer raise `x-delivery-count`), so it is redelivered indefinitely. For a legitimately slow handler, set a large finite `Timeout` instead.
6. **Keep slow recoveries out of the delivery.** For failures that take minutes to clear, prefer a short `Timeout`, no in-process retries and the [dead-letter reprocessor](dead-letter.md#dead-letter-reprocessor): the delivery is settled at once and the broker timeout stays short. In-process [retries](#retry-policies) (deprecated) hold the delivery unacknowledged for the whole cycle.
7. **On RabbitMQ 4.3+, use a quorum queue if you want a broker-side bound**: classic queues there have no consumer timeout at all.
8. **Changing `Timeout` later does not update an existing queue's `x-consumer-timeout`**: see the warning at the end of this section.

The rest of this section explains each mechanism in detail.

The `Timeout` setting (in `ConsumerSettings`) bounds how long a single handler invocation may run before being cancelled:

```csharp
c.Timeout = TimeSpan.FromSeconds(30); // default
```

Internally it drives a `CancellationTokenSource` passed to `HandleAsync`. On timeout, the token is cancelled, the attempt fails, and the retry policy kicks in.

**Stuck-handler watchdog (v8.2.0)**

Cancelling the token only works if the handler honors it. A handler that ignores its `CancellationToken`
(e.g. a blocking call with no timeout) would otherwise hold its prefetch slot forever. To prevent that, the
consumer waits at most `Timeout` **plus a 30-second grace period** (fixed, not configurable) for the handler
task to complete. If it still hasn't:

- The wait is **abandoned**: the prefetch slot is reclaimed and the message follows the failure path.
- The abandoned handler task keeps running detached — it is observed, and its eventual failure is logged.
  Its DI scope is disposed, so late access to scoped services from the runaway handler may throw.
- The watchdog is **skipped when `Timeout <= 0`** (e.g. `Timeout.InfiniteTimeSpan`): an infinite timeout
  means there is no cancellation to honor.

> **⚠️ Abandonment is treated as a permanent failure, not a timeout.** A *cooperative* timeout (the handler
> honors the token and throws `OperationCanceledException`) is classified as **transient**: it is retried
> in-process and remains eligible for the [dead-letter reprocessor](dead-letter.md#dead-letter-reprocessor).
> An *abandoned* handler surfaces as a non-transient `RabbitFlowHandlerAbandonedException` (a `RabbitFlowException`;
> v8.4.0, previously the base type): remaining in-process retries are skipped, the dead-letter envelope is written
> with `isTransient = false` and that exception type, an `Error` log line with the message id is written, the
> failure metric carries `reason = abandoned`, and the reprocessor parks or discards it instead of re-enqueuing. A
> handler that ignores cancellation is a bug to fix, not a condition to retry.

**How long a delivery can stay unacknowledged**

With `T` = `Timeout`, `G` = the 30s grace, `I` = `RetryInterval` and `N` = `MaxRetryCount + 1` attempts, each path holds the delivery unacknowledged for at most:

| Path | One attempt | Whole delivery (worst case) | Then |
|------|-------------|-----------------------------|------|
| Handler completes in time | `T` | `T` | ack |
| Late success: the handler swallows the cancellation and completes before the watchdog | `T + G` | `T + G` | ack (treated as success) |
| Cooperative cancellation: the handler throws `OperationCanceledException` after its token fires | `T + G` | `N × (T + G) + (N − 1) × I` | retried as a transient timeout; dead-lettered after the last attempt |
| Transient error (`RabbitFlowTransientException`, transient HTTP, …) | `< T` | `N × T + (N − 1) × I` | retried; dead-lettered after the last attempt |
| Abandoned handler: ignores cancellation until the watchdog gives up | `T + G` | `(N − 1) × (T + G) + (N − 1) × I + (T + G)` when earlier attempts were cooperative timeouts | dead-lettered at once as non-transient; no further attempts |
| Non-transient error | `< T` | `< T` | dead-lettered at once |

The longest row is `N × (T + G) + (N − 1) × I`. Dead-lettering itself (publishing the envelope, its confirm and the ack of the original) happens after it, still unacknowledged; the derived `x-consumer-timeout` below budgets both.

**Server-side timeout (`x-consumer-timeout`)**

RabbitMQ enforces a delivery acknowledgement timeout: a delivery not acked within the window is returned to the queue for redelivery, and the broker intervenes on the consumer. Because the in-process retry policy holds the delivery **unacknowledged** for the whole retry cycle, that cycle must fit inside this window. What exactly happens depends on the broker version and queue type:

| Broker | Queues that evaluate it | Who evaluates it | When it fires |
|---|---|---|---|
| 3.12 – 3.13 | all (global default 30 min; `x-consumer-timeout` per queue) | the channel | the **channel is closed** (`PRECONDITION_FAILED`); the library's channel recovery re-subscribes |
| 4.0 – 4.2 | all for the global setting; `x-consumer-timeout` accepted on quorum, **rejected on classic** | the channel | the channel is closed; same recovery |
| 4.3+ | **quorum only** — classic queues and streams never evaluate it | the queue | the **consumer is cancelled** (`basic.cancel`), the channel stays open; the library re-subscribes (v8.4.0). `consumer_timeout_response = close_channel` (4.3.7+) restores the channel close |

- The timeout was introduced in **RabbitMQ 3.8.15** as a **broker-wide** setting (`consumer_timeout`), default 15 minutes — raised to **30 minutes in 3.8.17**, which is still the default on 4.x, 4.3+ included. The 24 hours mentioned in the 4.3.3 release notes is not the default: it is the fallback a 4.3.3+ broker uses when `consumer_timeout` is set to a non-integer value (for example `undefined` in `advanced.config`, which older brokers treated as "no timeout"). A per-queue `x-consumer-timeout` or `consumer-timeout` policy (the lower of the two when both are set) takes precedence over the broker-wide value, so on quorum queues the argument the library derives is what bounds the delivery. The broker evaluates the timeout periodically, so a delivery can outlive the configured value by up to the evaluation interval.
- Overriding it **per queue** via the `x-consumer-timeout` queue argument requires **RabbitMQ 3.12+**. On older brokers the argument is ignored and only the broker-wide `consumer_timeout` applies — so a long `Timeout` there must be accommodated by raising `consumer_timeout` on the broker instead.

When `AutoGenerate = true`, EasyRabbitFlow sets `x-consumer-timeout` on the declared queue to accommodate the full retry cycle, including the grace period of every attempt:

```text
x-consumer-timeout = max(60s, (Timeout + 30s grace) × (MaxRetryCount + 1) + RetryInterval × MaxRetryCount + 10s)
```

The grace is budgeted per attempt, not once: a handler that honours cancellation slowly can take almost `Timeout + 30s` on each attempt and still be retried as a transient timeout, so the delivery stays unacknowledged for the whole of that cycle. The final 10s covers dead-lettering after the last attempt (publishing the envelope, its confirm, a possible re-declare of the dead-letter topology, the ack). The value is floored at 60s because RabbitMQ rejects consumer timeouts below one minute. This keeps the broker-side policy in sync with your configured retry behavior, so the broker never kills a consumer mid-retry.

When `Timeout <= 0` (e.g. `Timeout.InfiniteTimeSpan`, "no handler timeout") the argument is **not emitted** (v8.4.0; earlier versions derived a 60s broker timeout from the negative value, cutting off handlers that were meant to run indefinitely). The broker's own consumer timeout — the global `consumer_timeout` or a `consumer-timeout` policy — still applies where the queue type evaluates it, so a quorum queue on 4.3+ with a global timeout can still cancel a very long handler; the consumer then re-subscribes (see [Channel & connection recovery](#channel--connection-recovery)) and the delivery is redelivered. On 4.3+ that return does not raise `x-delivery-count`, so neither `MaxBrokerDeliveries` nor the broker's `delivery-limit` bounds it: with no `Timeout`, a message whose handler always outlives the broker's timeout is redelivered indefinitely.

> **ℹ️ RabbitMQ 4.x and classic queues**
>
> RabbitMQ 4.x rejects the per-queue `x-consumer-timeout` argument on **classic** queues (`PRECONDITION_FAILED - invalid arg 'x-consumer-timeout' ... of queue type rabbit_classic_queue`); it is only accepted on **quorum** queues. When a classic queue is declared, EasyRabbitFlow **detects the rejection, re-declares the queue without `x-consumer-timeout`, and continues** (logging a warning once). This is automatic and needs no configuration — `AutoGenerate` works on both 3.13 and 4.x.
>
> What then bounds a slow ack on a classic queue depends on the minor version (see the table above): on **4.0–4.2** the broker-wide `consumer_timeout` (30 min by default) still applies and closes the channel; from **4.3** classic queues have **no consumer timeout at all**, so a slow handler is never interrupted by the broker. Your per-message `Timeout` above is enforced **by the library** (a `CancellationToken`) in every case. If you need a broker-enforced per-queue timeout on 4.3+, use a quorum queue (`QueueType = QueueType.Quorum`): the derived `x-consumer-timeout` is accepted there, and the consumer re-subscribes after the resulting cancel.

> **⚠️ `x-consumer-timeout` only applies when the queue is first created**
>
> Changing `Timeout`, `MaxRetryCount`, or `RetryInterval` on a queue that already exists does **not** update its `x-consumer-timeout`: the queue keeps its original value. What the broker does with the new value depends on the queue type: **classic queues on 3.x silently ignore it** (no `PRECONDITION_FAILED`, no warning), whereas **quorum queues on 4.x compare it** like any other argument (`PRECONDITION_FAILED - inequivalent arg 'x-consumer-timeout'`) and the consumer adopts the existing queue with a warning (see the **Queue arguments are immutable in RabbitMQ** note below). **To make the change take effect, delete and recreate the queue** (safe since v8.4.0: the consumer re-subscribes by itself) or apply a [`consumer-timeout` policy](https://www.rabbitmq.com/docs/consumers#per-queue-delivery-timeout) on the broker, which *can* be changed in place.

#### Channel & connection recovery

EasyRabbitFlow manages consumer recovery itself rather than relying on the RabbitMQ client's built-in automatic/topology recovery (which is deliberately disabled on the connections it owns — running both at once makes the client re-bind/re-consume a recorded topology that doesn't include the main queue, surfacing as a `404 NOT_FOUND` on reconnect). When the broker closes a channel or connection (timeout exceeded, protocol violation, network blip, etc.), the consumer re-establishes the connection and channel, re-applies QoS, **re-declares its full topology**, and re-subscribes — with exponential backoff between attempts, seeded by `NetworkRecoveryInterval` and capped at 30s or the configured interval when larger (with the default 10s: 10s, 20s, 30s, 30s…).

Recovery after a connection the broker force-closed used to take an extra 20s: disposing the dead channel waited for the client's `ContinuationTimeout`. Since v8.4.0 the old channel is disposed off the recovery path, so the consumer is typically back within the first backoff interval.

**Broker-initiated consumer cancellation (`basic.cancel`, v8.4.0).** The broker can also cancel just the consumer and leave its channel open: when the queue is deleted, when the node hosting the queue goes away, or — on RabbitMQ 4.3+ **quorum queues** — when a delivery exceeds the consumer acknowledgement timeout (4.3 sends `basic.cancel` instead of closing the channel; see [Server-side timeout](#consumer-timeout)). No channel or connection event fires in that case, so until v8.4.0 the consumer silently stopped receiving until the process restarted. The consumer now listens for the cancellation, logs a warning naming the queue and consumer tag, increments `easyrabbitflow.consumer.cancellations`, drops the channel and runs the same recovery as above (re-declare topology, re-subscribe), serialized with any recovery already in flight so there are never two consumers on the queue. Deleting an auto-generated queue from the management UI is therefore safe: it is recreated with the current arguments and consumption resumes within a few seconds.

**At-least-once, spelled out.** Whenever the broker takes deliveries back — a cancel, a dropped connection, a channel closed by the broker, or a shutdown that outlasts the 30s drain — the handlers that were running keep running to completion, but their acknowledgements can no longer succeed and the same messages are delivered again, possibly while the first handler is still at work. The library never acknowledges a message it did not process, so nothing is lost; the price is duplicates. Handlers must therefore be idempotent by `MessageId` (a unique constraint or a lock, not a check-then-act), and `RabbitFlowMessageContext.Redelivered` / `DeliveryCount` tell a handler that a copy may have run before.

The same mechanism protects **startup**: if the broker is unreachable or the initial setup (declare, bind,
subscribe) fails when the host starts, the host still starts — the failure is logged and the consumer keeps
retrying in the background on the same backoff until it succeeds (requires `AutomaticRecoveryEnabled = true`).

On **shutdown**, the consumer stops accepting new deliveries and waits up to 30s for in-flight handlers to
finish, so their messages are acked or dead-lettered instead of being redelivered on the next start. A delivery
still waiting for a prefetch slot at that point is released unacked (the broker redelivers it once the channel
closes), so shutdown stays bounded even when the slot is held by a handler that ignores cancellation (v8.4.0;
before, such a wait could hold the channel open until the [watchdog](#consumer-timeout) abandoned the handler).
A recovery pass that is running at that moment (for example in its backoff while the broker is down) is cancelled
by the shutdown and the consumer waits at most 10s, or until the host's shutdown token fires, for it to leave;
if it does not, the channel is disposed without the orderly close and a warning is logged.

This is controlled by two `HostSettings` knobs:

- `AutomaticRecoveryEnabled` (default `true`) — when `false`, a dropped consumer stays down until the application restarts.
- `NetworkRecoveryInterval` (default `10s`) — the base delay for the recovery backoff.

See [Configuration](configuration.md#host-settings).

> **⚠️ Queue arguments are immutable in RabbitMQ**
>
> Arguments that RabbitMQ verifies for equivalence — `x-dead-letter-exchange`, `x-dead-letter-routing-key`, `x-message-ttl`, `x-max-length`, `x-max-priority`, `durable`, and any custom `Args` — cannot be changed by redeclaring an existing queue: the broker rejects the declare with `PRECONDITION_FAILED`. (The derived `x-consumer-timeout` is verified too on 4.x quorum queues, and silently *ignored* on 3.x classic queues; see the note above.)
>
> EasyRabbitFlow does **not** fail the consumer over this. It declares the queue on a throwaway channel, and if RabbitMQ rejects it with `PRECONDITION_FAILED`, the consumer checks whether the queue actually exists: if it does, it **adopts the existing queue as-is and keeps running**, logging a warning that names the queue — a running consumer is safer than one that won't start and silently leaves the system idle. If the broker rejected the declare and the queue does **not** exist (e.g. an argument the broker refuses to accept, or a conflicting policy), there is nothing to adopt: setup fails and the consumer stays in its recovery loop until the configuration is fixed.
>
> This adopt-on-mismatch protection covers the **main queue and the auto-generated dead-letter queue** (the latter since v8.4.0, so a dead-letter queue that already exists with another `x-queue-type` is adopted instead of blocking the consumer). The dead-letter exchange, [replicas](dead-letter.md#dead-letter-replicas), and the main exchange are declared on the consumer's own channel — a `PRECONDITION_FAILED` on any of those closes the channel and the consumer cannot start (it retries in the recovery loop). Resolve mismatches on those objects on the broker side.
>
> A related edge case is handled too: if the existing queue is a **stale `auto-delete` (or `x-expires`) queue** left over from a previous run — for example a broker that survived an app restart, as is common with Aspire — the broker may delete it the moment its previous consumer disconnects, *right as the new consumer is starting*. That produces an "adopt" (the queue still exists at declare time) immediately followed by a `404 NOT_FOUND` when binding/consuming (it's already gone). The consumer detects the 404 during setup, **recreates the queue with the current arguments** (it no longer exists, so there is no mismatch) and resumes — rather than crashing host startup.
>
> To actually apply the new arguments to a queue that *does* persist:
>
> **Options:**
>
> 1. Delete the queue and let EasyRabbitFlow recreate it.
> 2. Apply the change via a broker policy where the argument supports it (e.g. a [`consumer-timeout` policy](https://www.rabbitmq.com/docs/consumers#per-queue-delivery-timeout)), and add the matching value to `AutoGenerateSettings.Args` so the declare matches.
> 3. Set `AutoGenerate = false` and manage the queue yourself.

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
| `Redelivered` | bool | Whether the broker redelivered this message (any queue type). A single redelivery is normal after a deploy or a dropped connection. |
| `DeliveryCount` | int? | How many returns of this delivery the **broker** counted before handing it over again, from the `x-delivery-count` header that **quorum queues** set on redeliveries: every return on RabbitMQ 4.0–4.2 (consumer timeout, closed channel or connection, requeue); from 4.3 only failed deliveries (crash, dropped connection), so a redelivery after a consumer timeout arrives with `Redelivered = true` and the count unchanged. `null` on the first delivery and always on classic queues. Use it to make side effects idempotent on retries; the consumer stops invoking the handler at `MaxBrokerDeliveries` (default 5). Unrelated to `ReprocessAttempts`. |
| `ReprocessAttempts` | int | Number of times this message was re-enqueued from the DLQ by the [Dead-Letter Reprocessor](dead-letter.md#dead-letter-reprocessor). `0` for messages that have never been reprocessed. |
| `DeliveryMode` | MessageDeliveryMode? | AMQP `delivery-mode`. `null` when the publisher did not set it explicitly (the broker treats absent as `Transient`). |
| `Type` | string? | AMQP `type` — typically a logical event name independent of the .NET CLR type. `null` when not set on the wire. |
| `AppId` | string? | AMQP `app-id` — identifies the publishing application. `null` when not set on the wire. |
| `Expiration` | TimeSpan? | AMQP `expiration` decoded from its millisecond string. `null` when not set on the wire. The broker enforces the TTL; this value is informational. |
| `Priority` | byte? | AMQP `priority`. `null` when not set on the wire. Only meaningful on priority queues (see [`MaxPriority`](#auto-generate-topology)). |
| `Timestamp` | DateTimeOffset? | AMQP `timestamp` decoded from AMQP Unix seconds. `null` when not set on the wire. |
| `ReplyTo` | string? | AMQP `reply-to`. `null` when not set on the wire. |
| `ContentType` | string? | AMQP `content-type`. `null` when not set on the wire. |

> **Note:** `RabbitFlowMessageContext` is immutable — all properties are read-only and populated automatically from `BasicDeliverEventArgs` before `HandleAsync` is invoked.
