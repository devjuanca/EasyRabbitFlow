# Payments — Dead-letter replicas

Illustrates **`DeadLetterReplicas`**: extra queues bound to a consumer's auto-generated dead-letter exchange so that every dead-lettered message is delivered as a copy to multiple destinations, each consumable (or inspectable) in isolation.

## What it demonstrates

- **Auto-generated dead-letter topology** — `PaymentConsumer` declares the main queue plus the dead-letter exchange + DLQ.
- **Two dead-letter replicas**:
  - `payments-audit` — long-retention copy (30-day TTL). Nothing consumes it; operators inspect it via the RabbitMQ management UI.
  - `payments-alerts` — live alerting feed consumed by `PaymentAlertsConsumer`.
- **Independent consumption** — the alerts consumer can fail, restart, or fall behind without affecting the primary DLQ or the audit copy.
- **`DeadLetterEnvelope` shape** — replicas receive the envelope (with exception type, message, stack trace, etc.) because `ExtendDeadletterMessage = true` on the primary consumer.

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

## Triggering a failure

`PaymentConsumer` throws — and the message dead-letters with copies to all three queues — when either of these holds:

- `shouldFail = true`
- `amount <= 0`

Otherwise the message is processed successfully and no replica traffic occurs.

## Endpoints

| Method | Path | What it does |
|--------|------|--------------|
| POST | `/payments` | Publish a PaymentEvent. Body controls success/failure. |

See [Payments.http](Payments.http) for ready-to-run requests covering the happy path and two failure modes.
