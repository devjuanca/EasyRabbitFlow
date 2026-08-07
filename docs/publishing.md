## Publishing Messages

Inject `IRabbitFlowPublisher` to publish messages. All single-message publishes use **publisher confirms** — the `await` only completes after the broker confirms receipt. Confirm-channels are rented from a bounded pool sized by [`MaxPooledChannels`](configuration.md#publisher-options), so concurrent single-message fan-outs don't pay a channel-open per publish.

**Exception contract:** publish methods never throw for broker or connection failures — every failure (unreachable broker, closed connection, timeout awaiting the confirm, cancellation) surfaces as a result with `Success = false` and the exception in `Error`. The only exceptions that propagate to the caller are argument validation ones, thrown before any I/O: `ArgumentNullException` when `message` is `null`, and `ArgumentException` when the batch collection is `null` or empty.

> **`Success` means "accepted by the broker", not "routed to a queue".** Publishes are sent with `mandatory: false`, so a message published to an existing exchange whose routing key matches no binding is silently dropped by the broker — and still returns `Success = true`. This is the most common surprise with [application-owned exchanges](configuration.md#application-owned-exchanges): the producer declares the exchange, but until a consumer binds a queue, routed messages go nowhere.

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

- **`Transactional`** (default): All-or-nothing — if any message fails, the entire batch is rolled back. Uses AMQP transactions, **not** publisher confirms: `Success = true` means the `tx.commit` completed, not that each message was individually confirmed.
- **`Confirm`**: Each message is individually confirmed by the broker — a mid-batch failure does not roll back previous messages.

Batches always open their own dedicated channel; they never use the single-message channel pool.

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
| `MessageIds` | IReadOnlyList\<string\> | Identifiers per message (in input order) — produced by `messageIdSelector` when supplied or auto-generated GUIDs otherwise. On success, one entry per message. On failure, contains only the ids generated before the failing message (while `MessageCount` still reports the full batch size); in `Transactional` mode those messages were rolled back and never delivered. |
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
| `Priority` | byte? | `null` | AMQP `priority`. Written to the wire as-is (the 0–9 range is RabbitMQ convention, not validated by the library). **Requires the destination queue to be declared with [`MaxPriority`](consumers.md#auto-generate-topology)**; without it the broker silently delivers messages FIFO. |
| `Timestamp` | DateTimeOffset? | `null` | AMQP `timestamp` — serialized as Unix seconds. |
| `ReplyTo` | string? | `null` | AMQP `reply-to` — names the queue/exchange a consumer should reply on (RPC-style flows). |
| `ContentType` | string? | `null` | AMQP `content-type`. Not set by default to preserve historical wire format; set to `"application/json"` if your consumers need it. |
| `Headers` | IDictionary? | `null` | AMQP headers table. Use for business metadata or routing on headers exchanges. |
| `JsonOptions` | JsonSerializerOptions? | `null` | Per-call override for the JSON serializer. When `null`, the publisher uses the globally configured serializer. |

Consumers read every field above (except `JsonOptions`, which is publisher-side only) via [`RabbitFlowMessageContext`](consumers.md#message-context).

> **Priority footgun:** setting `options.Priority` on a queue that was **not** declared with `x-max-priority` is silently ignored by the broker — messages are delivered in FIFO order regardless. For auto-generated queues, configure [`AutoGenerateSettings.MaxPriority`](consumers.md#auto-generate-topology); for queues declared externally, add `x-max-priority` to the queue arguments yourself.
