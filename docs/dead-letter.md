# Dead-Letter Handling

## Dead-Letter Delivery Guarantee

The library protects the envelope publish-before-ack path with **publisher confirms and mandatory routing**. It acknowledges the original only after the new publication is confirmed without a return. This protects against acknowledging an unroutable envelope; it is not an unconditional guarantee against message loss.

When the broker returns an envelope, the consumer re-declares the dead-letter queue, exchange, binding and replicas and publishes again. If the second publication is returned or the re-declaration fails, it requeues the original after a pause of `NetworkRecoveryInterval` (at least 1s). Re-declaration uses a separate channel so a rejected declaration cannot close the consumer channel. Mandatory routing requires at least one destination: an existing replica can satisfy it even when the primary DLQ is absent.

For other envelope failures, including serialization errors and unconfirmed publications, the consumer falls back to broker-native `nack(requeue: false)` dead-lettering. That path also applies when envelopes are disabled. Its safety depends on RabbitMQ topology and the queue's dead-letter strategy: native dead-lettering can lose messages when its destination is unavailable. See [RabbitMQ dead-letter safety](https://www.rabbitmq.com/docs/dlx#safety).

The reprocessor uses **confirms plus mandatory routing** for every re-enqueue and park, independently of `PublishOptions.Mandatory`. A missing main queue, or a parking queue deleted after its existence check, causes a return and skips the DLQ ACK. Closing the cycle's channel makes the unacknowledged original available in the DLQ for a later cycle. The reprocessor does not recreate the main queue; the consumer's topology recovery or the operator must restore it.

Enable the library-managed dead-letter path with:

- `AutoGenerate = true` and `GenerateDeadletterQueue = true` (the default), so the DLQ and its exchange exist. Without them, failures fall back to a plain `nack(requeue: false)`; if the queue has no `x-dead-letter-exchange` of its own, the broker **discards** the message.
- `AutoAckOnError = false` (the default). With `AutoAckOnError = true`, failed messages are acknowledged and dropped by design — nothing reaches the DLQ.

Duplicates remain possible: the broker may accept a publication even when its confirmation is lost, or the original may be redelivered if its ACK does not reach the broker. Queue deletion, expiry, retention policies and broker delivery limits remain outside these protections. Make consumers idempotent; see [Idempotency](publishing.md#idempotency).

**Durability.** New parking queues are declared **durable**; an existing parking queue is adopted as-is. The auto-generated DLQ inherits `DurableQueue` and `AutoDeleteQueue` from `AutoGenerateSettings`. Envelopes and parked messages are published **persistent** (`DeliveryMode = 2`), but broker-native dead-lettering retains the original delivery mode, and replays restore it. For restart protection, verify the actual queues are durable and the broker data directory uses persistent storage (for example a volume at `/var/lib/rabbitmq`). Persistence does not prevent administrative deletion, expiry or retention-policy eviction.

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
  "reprocessAttempts": 0,
  "isTransient": true,
  "properties": {
    "deliveryMode": 2,
    "type": "OrderCreated",
    "appId": "checkout-svc",
    "priority": null,
    "contentType": "application/json",
    "replyTo": null,
    "headers": { "traceparent": "00-..." }
  }
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

The **dead-letter reprocessor** is the library's retry mechanism: attach it to a consumer and it runs on a fixed cadence, drains the consumer's auto-generated dead-letter queue, and re-publishes each transient failure to the main queue until a maximum attempt count is reached — without holding the original delivery open between cycles. During a cycle, a fetched DLQ message remains unacknowledged until its transfer is confirmed. (In-process retries, `ConfigureRetryPolicy`, are deprecated since v8.4.0.)

```csharp
cfg.AddConsumer<OrderConsumer>("orders-queue", c =>
{
    c.AutoGenerate = true;                       // required
    c.ExtendDeadletterMessage = true;            // forced when reprocessor is enabled

    c.ConfigureDeadLetterReprocess(r =>
    {
        r.Enabled = true;
        r.MaxReprocessAttempts = 5;                      // re-enqueues DLQ → main (⇒ up to 6 deliveries absent redeliveries/in-process retries)
        r.Interval = TimeSpan.FromHours(3);              // run every 3 hours
        // r.MaxMessagesPerCycle = 500;                  // optional safety cap; defaults to int.MaxValue (drain entire snapshot)
        // r.FinalAction = DeadLetterFinalAction.Discard; // drop exhausted/permanent instead of parking (default: Park)
        // r.ParkingMessageTtl = TimeSpan.FromDays(7);    // age out parked messages after 7 days (default: no TTL)
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

If a message exhausts its reprocess budget, the reprocessor moves it to the **parking queue** (`{queue}-deadletter-parking`, declared durable by the reprocessor itself) with `reprocessAttempts` reflecting the final count — so it remains visible in any RabbitMQ client and is not silently dropped, and the DLQ stays free for messages that are still actionable. Permanent and malformed messages are parked the same way, once, instead of rotating through the DLQ on every cycle — their body is parked **unchanged** (the counter is not rewritten; only exhausted messages get the final count stamped). Parked messages are published **persistent** (`DeliveryMode = 2`) onto the durable parking queue, so they survive a broker restart; for permanent/malformed messages the original AMQP properties (`MessageId`, `CorrelationId`, headers, …) are preserved as well.

The parking queue is created **on demand the first time a cycle actually needs to park a message** (the consumer never declares it; a cycle that re-enqueues or discards everything never creates it), so it never clutters the broker unless it's actually used. The reprocessor probes it with a passive declare: if the queue already exists it is used **as-is**, so deliberate operator settings (a TTL to age out old failures, a quorum queue type, a `max-length`, …) are respected and the reprocessor never fails with `PRECONDITION_FAILED` fighting over arguments. To apply different arguments, delete the queue and let the reprocessor recreate it with defaults (durable, the dead-letter queue's type from [`DeadLetterQueueType` / `QueueType`](consumers.md#queue-types) — or the broker's `default_queue_type` when neither is set — plus `x-message-ttl` if `ParkingMessageTtl` is set).

### Discarding instead of parking

By default every terminal message is parked. Set `FinalAction = DeadLetterFinalAction.Discard` to instead **drop** exhausted and permanent messages — they are acknowledged off the DLQ without being re-published anywhere. This suits high-volume failures that carry no recovery value and where parking would only accumulate noise. In `Discard` mode the parking queue is only ever created if a **malformed** or a **poisoned** message appears, because:

- **Malformed messages are always parked, never discarded** — regardless of `FinalAction`. Their bytes could not be deserialized into an envelope, so dropping them would lose data with no recoverable trace.
- **Poisoned messages are always parked too** (v8.4.0) — envelopes written with `RabbitFlowPoisonMessageException` by the [poison guard](consumers.md#registering-consumers) (`MaxBrokerDeliveries`). The handler never ran, so their only fault may have been a run of restarts; they are kept for inspection.

These are the two terminal categories the discard switch does not govern.

Discarded messages are not silently invisible to operators: each one increments the `easyrabbitflow.messages.discarded` counter (tagged `reason = exhausted | permanent`) and is logged at debug level.

### Aging out parked messages

Set `ParkingMessageTtl` to a `TimeSpan` to have the reprocessor declare the parking queue with `x-message-ttl`, so parked messages are dropped by the broker once they reach that age.

> **The TTL is only applied when the reprocessor *creates* the parking queue.** If the parking queue already exists, it is adopted as-is and the TTL is **not** applied (the reprocessor never redeclares it to avoid `PRECONDITION_FAILED`). To apply a new or changed `ParkingMessageTtl` to a parking queue that already exists, **delete the queue** and let the reprocessor recreate it on the next cycle that needs to park a message.

Note that a parking queue with a TTL and no dead-letter target drops expired messages, so combining `Park` with `ParkingMessageTtl` is effectively a delayed discard.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | bool | `true` | Whether the reprocessor is active for this consumer |
| `MaxReprocessAttempts` | int | `3` | Maximum **re-enqueues** from DLQ back to main queue. Counts reprocesses only — not the original delivery — so `N` allows `N + 1` processing deliveries before parking in the normal path. In-process retries and broker redeliveries can cause additional handler executions. Minimum `1`. |
| `Interval` | TimeSpan | `3h` | Time between reprocessor runs. **Minimum 10 minutes** (hard floor — use the in-handler `RetryPolicy` for tighter retry cadences). The first cycle runs one full `Interval` **after startup** — with the default, nothing is reprocessed during the first 3 hours. |
| `MaxMessagesPerCycle` | int | `int.MaxValue` | Limits fetches to the smaller of this value and the initial ready-message count. This bounds work; concurrent consumers or reprocessors can change which messages are fetched. |
| `FinalAction` | `DeadLetterFinalAction` | `Park` | What to do with terminal **exhausted**/**permanent** messages: `Park` (move to the parking queue) or `Discard` (ack off the DLQ and drop). Does **not** apply to malformed or poisoned messages, which are always parked. |
| `ParkingMessageTtl` | TimeSpan? | `null` | When set, the parking queue is created with `x-message-ttl` so parked messages age out after this duration. Applies only on a queue the reprocessor creates; an existing queue is adopted as-is. Must be greater than zero. |

**Constraints:**

- Requires `AutoGenerate = true`. If the consumer manages its own topology, the reprocessor is silently disabled with a warning. `GenerateDeadletterQueue` must also stay `true`: with no auto-generated DLQ the reprocessor still starts, but every cycle fails to read the queue and is skipped with a warning.
- Re-enqueued messages are published **directly to the main queue** (via the default exchange), bypassing the consumer's exchange and any bindings — subscribers bound to the main exchange do not see replays, only the owning queue does.
- Each cycle opens its own short-lived connection (named `reprocessor_{queue}`) and closes it when the cycle ends — periodic connection churn in the management UI is expected.
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

For every inbound message, the consumer runs a cheap byte-level fingerprint scan looking for the two most distinctive envelope keys (`"messageData"` and `"exceptionType"`). Only when the fingerprint matches does it parse the body as `DeadLetterEnvelope`, extract the inner `MessageData`, and use it as the actual payload. If the envelope carried a `MessageId` or `CorrelationId`, those are used for tracing and for the new envelope written on a re-failure when AMQP didn't already provide them; `RabbitFlowMessageContext` still reflects the AMQP properties of the inbound delivery.

A warning is logged whenever an unwrap fires, so you can spot manual replays in the logs:

```text
[RABBIT-FLOW]: Detected manually replayed DeadLetterEnvelope on queue orders-queue for OrderConsumer;
unwrapped inner payload. Prefer the DeadLetterReprocessor for replays.
```

**No double-wrapping on re-failure**

If the unwrapped message fails again with `ExtendDeadletterMessage = true`, the new DLQ entry wraps the **original payload**, not the inbound envelope — the dead-letter queue stays at depth 1 instead of accumulating nested envelopes across replays.

**Default is `false`**

The fingerprint check is fast, but it still runs on every message. Leave the flag off unless you actually need the safety net — i.e. unless your operations team manually replays messages from the DLQ. Production deployments that rely exclusively on the reprocessor never need to enable it.
