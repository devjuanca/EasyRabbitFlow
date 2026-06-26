# Dead-Letter Handling

## Dead-Letter Delivery Guarantee

When a message exhausts its retries it is guaranteed to leave the main queue and reach the DLQ — it is never silently dropped. The `DeadLetterEnvelope` is published on a **publisher-confirms** channel, so the `await` only completes once the broker has confirmed receipt; if the publish is not confirmed (or serialization fails), the consumer falls back to broker-native `nack` dead-lettering. The dead-letter reprocessor uses confirms too: a re-enqueue or park only acks the message off the DLQ after the broker confirms the new copy, so an unconfirmed publish leaves the message safely on the DLQ.

This closes the message-**loss** window. It does **not** provide exactly-once delivery: a confirmed publish whose subsequent ack fails is redelivered, so a message can reach the DLQ — or be reprocessed — more than once. As with any at-least-once broker, make consumers idempotent; see [Idempotency](publishing.md#idempotency).

**Durability end-to-end.** The auto-generated DLQ and parking queue are declared **durable**, and every envelope the library publishes — onto the DLQ when a message dead-letters, and onto the parking queue when it is exhausted/permanent/malformed — is published **persistent** (`DeliveryMode = 2`). A message therefore survives a broker restart at every resting point, including the (potentially hours-long) wait in the DLQ between reprocessor cycles. This requires the broker's data directory to live on persistent storage (e.g. a `StatefulSet` with a `PersistentVolumeClaim` on `/var/lib/rabbitmq`); on ephemeral storage the whole broker state is lost on pod restart regardless of durability flags.

## Dead-Letter Replicas

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

- **Replica queues are created automatically — they do not need to pre-exist.** Each one is declared and bound to the dead-letter exchange at consumer startup. The declare is idempotent: an existing queue with identical settings is reused, but one that already exists with different arguments makes the broker raise `PRECONDITION_FAILED`. To apply specific arguments (TTL, max-length, queue type, ...), set them in `Arguments` from the start rather than declaring the queue separately.
- The replica queues are bound with the same routing key as the primary DLQ, so they receive **every** dead-lettered message — there is no per-queue filtering.
- Requires `GenerateDeadletterQueue = true`. If `false`, the list is ignored and a warning is logged at startup.
- The [Dead-Letter Reprocessor](#dead-letter-reprocessor) reads only the primary DLQ (`{queue}-deadletter`). Messages re-enqueued by the reprocessor are not replicated a second time — only their original arrival on the DLQ is. Replica queues are independent and never drained by the reprocessor.
- Replica queue names are *not* validated against the [reserved substrings](consumers.md#reserved-name-substrings) — they are caller-owned destinations, not auto-generated topology.

## Dead-Letter Reprocessor

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
        r.MaxReprocessAttempts = 5;              // re-enqueues DLQ → main (⇒ up to 6 handler executions total)
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

If a message exhausts its reprocess budget, the reprocessor moves it to the **parking queue** (`{queue}-deadletter-parking`, declared durable by the reprocessor itself) with `reprocessAttempts` reflecting the final count — so it remains visible in any RabbitMQ client and is not silently dropped, and the DLQ stays free for messages that are still actionable. Permanent and malformed messages are parked the same way, once, instead of rotating through the DLQ on every cycle. Parked messages are published **persistent** (`DeliveryMode = 2`) onto the durable parking queue, so they survive a broker restart; for permanent/malformed messages the original AMQP properties (`MessageId`, `CorrelationId`, headers, …) are preserved as well.

The parking queue is created on demand the first time the reprocessor finds messages in the DLQ to drain (the consumer never declares it, and a cycle that finds the DLQ empty skips it), so it never clutters the broker unless it's actually used. The reprocessor probes it with a passive declare: if the queue already exists it is used **as-is**, so deliberate operator settings (a TTL to age out old failures, a quorum queue type, a `max-length`, …) are respected and the reprocessor never fails with `PRECONDITION_FAILED` fighting over arguments. To apply different arguments, delete the queue and let the reprocessor recreate it with defaults (durable, classic, no extra args).

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | bool | `true` | Whether the reprocessor is active for this consumer |
| `MaxReprocessAttempts` | int | `3` | Maximum **re-enqueues** from DLQ back to main queue. Counts reprocesses only — not the original delivery — so `N` allows up to `N + 1` total handler executions before parking (e.g. `1` ⇒ 2 executions). Minimum `1`. |
| `Interval` | TimeSpan | `3h` | Time between reprocessor runs. **Minimum 10 minutes** (hard floor — use the in-handler `RetryPolicy` for tighter retry cadences) |
| `MaxMessagesPerCycle` | int | `int.MaxValue` | Optional safety cap on messages drained per cycle. By default each cycle drains the whole DLQ snapshot taken at the start of the run; lower this only if you need an explicit ceiling. |

**Constraints:**

- Requires `AutoGenerate = true`. If the consumer manages its own topology, the reprocessor is silently disabled with a warning.
- Forces `ExtendDeadletterMessage = true` so the envelope (including `reprocessAttempts`) is available — a warning is logged if you set it to `false`.
- Operates only on the auto-generated dead-letter queue (`{queue}-deadletter`). Any [Dead-Letter Replicas](#dead-letter-replicas) bound to the same DLX are independent and never drained by the reprocessor.
- **Only transient failures are reprocessed.** A message is eligible only if its envelope's `isTransient` flag is `true` — classified at failure time by the same rules the in-handler `RetryPolicy` uses (see [Transient Exceptions](transient-exceptions.md#transient-exceptions-and-custom-retry-logic)): `RabbitFlowTransientException` and derived types, cancellation/timeout, and transient HTTP failures. Permanent failures (validation, deserialization, business-rule violations) are moved to the parking queue so they remain visible for inspection without churning through the DLQ. Envelopes from older versions (without the flag) fall back to exact type-name matching.
- Counter persistence: the reprocessor sets the AMQP header `x-reprocess-attempts` on every message it re-publishes. The consumer reads this header on receipt and seeds the new envelope with that count if processing fails again, so the counter survives the full cycle DLQ → main → DLQ.
- **AMQP properties are restored on replay.** The envelope captures the original message's `DeliveryMode`, `Type`, `AppId`, `Priority`, `ContentType`, `ReplyTo`, and headers (including `traceparent`) at failure time, and the reprocessor restores them when re-enqueueing — a persistent message stays persistent after a replay. Header values are preserved as strings (binary values are decoded as UTF-8).

## Manual DLQ Replay Safety Net

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
