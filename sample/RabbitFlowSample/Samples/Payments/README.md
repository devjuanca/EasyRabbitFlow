# Payments — Dead-letter replicas

Illustrates **`DeadLetterReplicas`**: extra queues bound to a consumer's auto-generated dead-letter exchange so that every dead-lettered message is delivered as a copy to multiple destinations, each consumable (or inspectable) in isolation.

## What it demonstrates

- **Auto-generated dead-letter topology** — `PaymentConsumer` declares the main queue plus the dead-letter exchange + DLQ.
- **Two dead-letter replicas**:
  - `payments-audit` — long-retention copy (30-day TTL). Nothing consumes it; operators inspect it via the RabbitMQ management UI.
  - `payments-alerts` — live alerting feed consumed by `PaymentAlertsConsumer`.
- **Independent consumption** — the alerts consumer can fail, restart, or fall behind without affecting the primary DLQ or the audit copy.
- **`DeadLetterEnvelope` shape** — replicas receive the envelope (with exception type, message, stack trace, etc.) because `ExtendDeadletterMessage = true` on the primary consumer.
- **Quorum queues and the poison guard** — explicit `QueueType = Quorum` on the main and dead-letter queues, `MaxBrokerDeliveries` lowered to 2 so the guard can be triggered by hand (see below).

## Topology

```
                        ┌────────────────────┐
                        │  payments-queue    │
                        └────────┬───────────┘
                                 │ (on failure, after retries)
                ┌────────────────▼─────────────────────┐
                │  payments-queue-deadletter-exchange  │
                └─────┬──────────────┬──────────┬──────┘
                      │              │          │
        ┌─────────────▼─────────┐ ┌──▼──────────┐ ┌──▼──────────────┐
        │ payments-queue-       │ │payments-    │ │ payments-alerts │
        │  deadletter (primary) │ │audit (30d)  │ │                 │
        └───────────────────────┘ └─────────────┘ └────┬────────────┘
                                                       │
                                                PaymentAlertsConsumer
                                                (live alerting)
```

## Queue types and the poison guard (v8.4.0)

`payments-queue` and its dead-letter queue are declared as **quorum** queues (`QueueType = QueueType.Quorum`); the
replicas keep their own explicit arguments. On a quorum queue the broker counts every time it takes a delivery back
(`x-delivery-count`), and the consumer sets `MaxBrokerDeliveries = 2` (library default: 5) so the guard can be
reproduced by hand:

1. Send the "Slow payment" request from [Payments.http](Payments.http) (`processingDelayMs = 20000`).
2. While the handler holds it, drop the connection from the broker:
   `docker exec <rabbit-container> rabbitmqctl close_all_connections demo`.
   The consumer recovers in about a second and the same payment is delivered again with `Redelivered=True,
   DeliveryCount=1` (the handler logs both; the library's log scope adds the message id).
3. Drop the connection once more. The third delivery arrives with `DeliveryCount=2`: the library logs a warning,
   does **not** run the handler, and dead-letters the envelope (`RabbitFlowPoisonMessageException`) to the DLQ and
   both replicas; the reprocessor will park it, never discard it.

On a classic queue there is no count, so the guard never fires: the payment would simply be processed again.

## Triggering a failure

`PaymentConsumer` throws — and the message dead-letters with copies to all three queues — when either of these holds:

- `shouldFail = true`
- `amount <= 0`

Otherwise the message is processed successfully and no replica traffic occurs. Every terminal failure also produces
one `Error` line from the library naming the message id, the reason and the exception.

## Endpoints

| Method | Path | What it does |
|--------|------|--------------|
| POST | `/payments` | Publish a PaymentEvent. Body controls success/failure. |

See [Payments.http](Payments.http) for ready-to-run requests covering the happy path and two failure modes.
