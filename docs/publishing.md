## Publishing Messages

Inject `IRabbitFlowPublisher` to publish messages. All single-message publishes use **publisher confirms** — the `await` only completes after the broker confirms receipt. Confirm-channels are rented from a bounded pool sized by [`MaxPooledChannels`](configuration.md#publisher-options), so concurrent single-message fan-outs don't pay a channel-open per publish.

**Exception contract:** publish methods never throw for broker or connection failures — every failure (unreachable broker, closed connection, timeout awaiting the confirm, cancellation) surfaces as a result with `Success = false` and the exception in `Error`. The only exceptions that propagate to the caller are argument validation ones, thrown before any I/O: `ArgumentNullException` when `message` is `null`, and `ArgumentException` when the batch collection is `null` or empty.

> **By default, `Success` means "accepted by the broker", not "routed to a queue".** `PublishOptions.Mandatory` defaults to `false`: an unroutable message can be dropped while the result reports success. This includes publishing directly to a missing queue and publishing to an existing exchange with no matching binding. Set `Mandatory = true` to require routing to at least one queue; see [Mandatory routing](#mandatory-routing). Publishing to a nonexistent exchange fails regardless of this option.

### Mandatory routing

`Mandatory` is an opt-in AMQP publication flag, independent of message persistence. It applies to both queue and exchange overloads, for single messages and every message in a batch. Omitting `options`, using `new PublishOptions()`, or explicitly setting `Mandatory = false` preserves the existing behavior, including event broadcasts that legitimately have no subscribers.

```csharp
using EasyRabbitFlow.Exceptions;
using EasyRabbitFlow.Settings;

var result = await publisher.PublishAsync(
    order,
    queueName: "orders-queue",
    messageId: $"order-{order.Id}-created",
    options: new PublishOptions
    {
        Mandatory = true,
        DeliveryMode = MessageDeliveryMode.Persistent
    });

if (result.Error is RabbitFlowUnroutableMessageException returned)
{
    // The caller decides whether to retain for retry, alert, or use another destination.
    logger.LogWarning("No route: {Code} {Text}, exchange={Exchange}, key={Key}",
        returned.ReplyCode, returned.ReplyText, returned.Exchange, returned.RoutingKey);
}
else if (!result.Success)
{
    logger.LogError(result.Error, "Publish failed for {MessageId}", result.MessageId);
}
```

The publisher reports a broker return through `Success = false` and `Error = RabbitFlowUnroutableMessageException`, consistently across modes. The exception includes `Exchange`, `RoutingKey`, `ReplyCode` (usually `312`) and `ReplyText` (usually `NO_ROUTE`). It is returned in the result, not thrown to the caller. The library does not automatically create a destination or retry the message.

| Operation | Behavior with `Mandatory = true` |
|---|---|
| Single publish | Waits for the confirm; a return produces a failed result. |
| Batch, `Confirm` | Stops at the first failed publish. Previously confirmed messages remain delivered. `MessageIds` contains only successful prior publishes. |
| Batch, `Transactional` | Collects returns before `tx.commit-ok`. Any return produces a failed result with the first return's details. Routed messages may already be delivered; no rollback is attempted after commit starts. `MessageIds` contains all submitted message IDs, including returned messages. |

For a batch, pass `options: new PublishOptions { Mandatory = true }` to `PublishBatchAsync`, with either channel mode. **A failed result does not mean that no messages were delivered.** Retrying a whole batch can duplicate messages; use stable IDs and idempotent consumers. Connection loss or cancellation during commit can also leave an unknown outcome.

Mandatory routing proves neither consumer processing nor the presence of every expected subscriber. A queue with no active consumers still counts as a route; a queue reached through an alternate exchange can also satisfy routing. Persistence, durable queues, TTL and retention policies remain separate concerns. See [RabbitMQ publishers](https://www.rabbitmq.com/docs/publishers) and [transaction semantics](https://www.rabbitmq.com/docs/semantics).

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
| `Success` | bool | Whether the broker confirmed the publication and, when `Mandatory = true`, did not return it. Does not indicate consumer processing. |
| `MessageId` | string | Identifier of the published message — caller-supplied via `messageId` parameter or auto-generated GUID. Always populated. |
| `Destination` | string | Target exchange or queue name |
| `RoutingKey` | string | Routing key used (empty for queue publishes) |
| `TimestampUtc` | DateTime | When the publish was executed |
| `Error` | Exception? | The exception if `Success` is `false` |

### Batch Publishing

Use `PublishBatchAsync` to publish multiple messages in a single operation. The `channelMode` parameter controls atomicity:

- **`Transactional`** (default): Publishes within one AMQP transaction. Failures before commit trigger an attempted rollback. Uses AMQP transactions, **not** publisher confirms: success requires a completed `tx.commit` and, when `Mandatory = true`, no returned messages. Returns detected at commit cannot undo messages already routed.
- **`Confirm`**: Each message is individually confirmed by the broker — a mid-batch failure does not roll back previous messages.

Batches always open their own dedicated channel; they never use the single-message channel pool.

> **Queue types and broker versions.** Both modes work against classic and quorum queues on RabbitMQ 3.13 and 4.x
> (covered by the integration suite on every CI leg). Keep in mind what RabbitMQ itself promises for
> `Transactional`: `tx.commit-ok` means every publish of the batch is visible, but the broker documents **no atomicity
> guarantee** across queues — a batch routed to several queues can land in some of them if the broker fails
> mid-commit — and none for a single queue either in failure scenarios. For business-critical fan-out prefer `Confirm`
> plus idempotent consumers. See [RabbitMQ semantics](https://www.rabbitmq.com/docs/semantics).

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
| `Success` | bool | Whether the batch completed under the selected mode and mandatory routing policy. Failure does not imply zero deliveries. |
| `MessageCount` | int | Number of messages in the batch |
| `MessageIds` | IReadOnlyList\<string\> | IDs in input order for messages whose publish call completed. Before-commit failures can leave a partial list. Transactional returns detected at commit leave the full list, including returned messages; it is not a delivery receipt. |
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
await publisher.PublishAsync(notification, "notifications", routingKey: "new", correlationId: requestId);

// Batch — same correlationId shared across all messages
await publisher.PublishBatchAsync(events, "orders-queue", correlationId: batchId);
```

The `correlationId` is set on `BasicProperties.CorrelationId` and received by consumers via `RabbitFlowMessageContext.CorrelationId`. For distributed tracing across services, see [Observability](observability.md#observability) — the W3C trace context travels in AMQP headers automatically.

### Per-Call AMQP Options (`PublishOptions`)

`PublishAsync` and `PublishBatchAsync` accept an optional `PublishOptions` parameter that bundles AMQP metadata, the `Mandatory` publication flag and an optional JSON serializer override. All fields are nullable (`null` = not written to the wire) except `DeliveryMode` (default `Transient`) and `Mandatory` (default `false`).

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
| `Mandatory` | bool | `false` | Require routing to at least one queue. A return produces a failed result; see [Mandatory routing](#mandatory-routing) for batch semantics. |
| `DeliveryMode` | MessageDeliveryMode | `Transient` | `Persistent` survives broker restart (queue must also be durable). `Transient` is in-memory only. |
| `Type` | string? | `null` | AMQP `type` — typically a logical event name independent of the .NET CLR type. |
| `AppId` | string? | `null` | AMQP `app-id` — identifies the publishing application. |
| `Expiration` | TimeSpan? | `null` | Per-message TTL. Broker discards or dead-letters after this window. Serialized as a millisecond string. |
| `Priority` | byte? | `null` | AMQP `priority`. Written to the wire as-is (the 0–9 range is RabbitMQ convention, not validated by the library). **Requires the destination queue to be declared with [`MaxPriority`](consumers.md#auto-generate-topology)**; without it the broker silently delivers messages FIFO. |
| `Timestamp` | DateTimeOffset? | `null` | AMQP `timestamp` — serialized as Unix seconds. |
| `ReplyTo` | string? | `null` | AMQP `reply-to` — names the queue/exchange a consumer should reply on (RPC-style flows). |
| `ContentType` | string? | `null` | AMQP `content-type`. Not set by default to preserve historical wire format; set to `"application/json"` if your consumers need it. |
| `Headers` | IDictionary? | `null` | AMQP headers table. Use for business metadata or routing on headers exchanges. |
| `JsonOptions` | JsonSerializerOptions? | `null` | Per-call override for the JSON serializer. When `null`, the publisher uses the globally configured serializer. |

Consumers read message metadata via [`RabbitFlowMessageContext`](consumers.md#message-context). `JsonOptions` and `Mandatory` control publishing and are not consumer metadata.

> **Priority footgun:** setting `options.Priority` on a queue that was **not** declared with `x-max-priority` is silently ignored by the broker — messages are delivered in FIFO order regardless. For auto-generated queues, configure [`AutoGenerateSettings.MaxPriority`](consumers.md#auto-generate-topology); for queues declared externally, add `x-max-priority` to the queue arguments yourself.

### Durability: three different guarantees

"Durable queue", "persistent message" and "publisher confirm" are often conflated. They are independent building blocks for surviving broker restarts. Also require routing where appropriate (`Mandatory = true`), retry uncertain outcomes and deduplicate at the consumer:

| Guarantee | Set where | What it covers | What it does **not** cover |
|---|---|---|---|
| **Durable queue** (`DurableQueue = true`, the default) | queue declaration | the queue *definition* survives a broker restart | its messages: transient messages on a durable queue are lost on restart |
| **Persistent message** (`PublishOptions.DeliveryMode = Persistent`) | each publish | the message is written to disk and survives a restart — **only** on a durable queue | that the broker received it (no confirm), or that a consumer processed it |
| **Publisher confirm** (always on for `PublishAsync`; see [Publishing](#publishing-messages)) | the publish channel | the broker confirmed the publication; with `Mandatory = true`, returns also fail the result | routing with `Mandatory = false`; processing by a consumer; persistence of transient messages on classic queues |

Every publish path of `IRabbitFlowPublisher` — with or without `messageId`/`correlationId`, with or without `PublishOptions` — sends the message **transient** unless `PublishOptions.DeliveryMode = MessageDeliveryMode.Persistent` is set; the default has never changed. The dead-letter envelope the consumer writes to the DLQ and the messages the reprocessor parks are the exceptions: they are always persistent, since they may wait hours for the next cycle.

**Quorum queues** store every message on disk regardless of `DeliveryMode` (replication requires it), so on a quorum queue a transient publish survives a restart anyway; keep setting `Persistent` for business messages so the guarantee does not depend on the queue type. See [Queue types](consumers.md#queue-types).
