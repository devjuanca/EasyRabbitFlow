# Notifications — Fanout pub/sub

Illustrates the **fanout exchange** pattern: one publish reaches every queue bound to the exchange, regardless of routing key. Two independent consumers — an email sender and a whatsapp deliverer — subscribe to the same `notifications` exchange and each pick the slice of the payload they care about.

## What it demonstrates

- **Fanout broadcast** — `POST /notifications/broadcast` reaches both consumers in parallel.
- **Direct-to-queue publish** — `POST /notifications/email` and `POST /notifications/whatsapp` bypass the exchange and target a specific queue.
- **Transactional batch publish** — `POST /notifications/broadcast/batch` publishes many events in a single AMQP transaction.
- **Transient classification** — `EmailConsumer` can signal transient failures (`RabbitFlowTransientException`), but this module does not configure in-process retries for it. Its failures go to the DLQ.
- **Dead-letter envelope + unwrap** — failed messages land on the DLQ wrapped in a `DeadLetterEnvelope`; manual replays are auto-unwrapped.
- **Dead-letter reprocessor** — `WhatsAppConsumer` re-injects dead-lettered messages back into the main queue on a schedule.
- **DI lifetimes** — `WhatsAppConsumer` logs singleton/scoped/transient GUIDs to verify EasyRabbitFlow honors DI scoping per message.

## Topology

```
              ┌────────────────────────┐
              │  notifications (fanout) │
              └────────────┬───────────┘
                  ┌────────┴────────┐
            ┌─────▼─────┐     ┌─────▼─────────┐
            │ email-queue│     │ whatsapp-queue│
            └─────┬─────┘     └─────┬─────────┘
            EmailConsumer        WhatsAppConsumer
```

Each consumer auto-generates its own dead-letter exchange + queue. `WhatsAppConsumer` additionally runs the dead-letter reprocessor.

## Endpoints

| Method | Path | What it does |
|--------|------|--------------|
| POST | `/notifications/broadcast` | Fanout publish — reaches email + whatsapp. |
| POST | `/notifications/email` | Direct publish to the email queue only. |
| POST | `/notifications/whatsapp` | Direct publish to the whatsapp queue only. |
| POST | `/notifications/broadcast/batch` | Transactional batch publish to the fanout exchange. |

See [Notifications.http](Notifications.http) for ready-to-run requests.
