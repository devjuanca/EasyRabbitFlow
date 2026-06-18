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
| `UnwrapDeadLetterEnvelopes` | bool | `false` | Defensive safety net: detect a `DeadLetterEnvelope` arriving on the main queue (e.g. from a manual DLQ replay) and process the inner payload. See [Manual DLQ Replay Safety Net](dead-letter.md#manual-dlq-replay-safety-net). |
| `DisableNameValidation` | bool | `false` | Skip validation of reserved substrings (`deadletter`, `-exchange`, `-routing-key`) against the queue name and any auto-generate names. See [Reserved Name Substrings](#reserved-name-substrings). Only honored when `AutoGenerate = false`. |

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
| `MaxPriority` | byte? | `null` | Declares the queue as a priority queue with this max priority via `x-max-priority`. Required for the broker to honor messages published with [`PublishOptions.Priority`](publishing.md#per-call-amqp-options-publishoptions); without it, priority is silently ignored and messages flow FIFO. Keep small (1–10). If `Args` already contains `x-max-priority`, that explicit entry wins. |
| `DeadLetterReplicas` | List&lt;DeadLetterReplica&gt; | empty | Extra queues to bind to the dead-letter exchange. See [Dead-Letter Replicas](dead-letter.md#dead-letter-replicas). |

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

> **⚠️ `x-consumer-timeout` only applies when the queue is first created**
>
> Unlike most queue arguments, RabbitMQ treats `x-consumer-timeout` as *optional* and **silently ignores a changed value when an existing queue is redeclared** — no `PRECONDITION_FAILED`, no error, no warning. So if you change `Timeout`, `MaxRetryCount`, or `RetryInterval` on a queue that already exists, the new `x-consumer-timeout` is **not** applied: the queue keeps its original value. **To make the change take effect, delete and recreate the queue** (or apply a [`consumer-timeout` policy](https://www.rabbitmq.com/docs/consumers#per-queue-delivery-timeout) on the broker, which *can* be changed in place). This is specific to `x-consumer-timeout`; other arguments (`x-dead-letter-exchange`, `x-max-priority`, custom `Args`) **are** verified on redeclare — see the **Queue arguments are immutable in RabbitMQ** note below.

**Channel recovery**

If the broker does close a channel (timeout exceeded, protocol violation, etc.), EasyRabbitFlow automatically recreates the channel, re-applies QoS, and re-subscribes the consumer — with exponential backoff between attempts (1s, 2s, 4s… capped at 30s). Connection-level closures are handled the same way.

> **⚠️ Queue arguments are immutable in RabbitMQ**
>
> Arguments that RabbitMQ verifies for equivalence — `x-dead-letter-exchange`, `x-dead-letter-routing-key`, `x-message-ttl`, `x-max-length`, `x-max-priority`, `durable`, and any custom `Args` — cannot be changed by redeclaring an existing queue: the broker rejects the declare with `PRECONDITION_FAILED`. (The derived `x-consumer-timeout` is the exception — it is silently *ignored* rather than rejected; see the note above.)
>
> EasyRabbitFlow does **not** fail the consumer over this. It declares the queue on a throwaway channel, and if RabbitMQ rejects it with `PRECONDITION_FAILED`, the consumer **adopts the existing queue as-is and keeps running**, logging a warning that names the queue — a running consumer is safer than one that won't start and silently leaves the system idle. To actually apply the new arguments:
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
| `Redelivered` | bool | Whether the broker redelivered this message |
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
