# SupportTickets — Priority queues (`MaxPriority` + `PublishOptions.Priority`)

Illustrates how to wire AMQP priority **end-to-end**: the queue is declared as a priority queue via `AutoGenerateSettings.MaxPriority`, the publisher tags each message with `PublishOptions.Priority`, and the consumer reads it back via `RabbitFlowMessageContext.Priority`.

## What it demonstrates

- **Queue-side `MaxPriority`** — `AutoGenerateSettings.MaxPriority = 9` emits the `x-max-priority` queue argument. Without it, `PublishOptions.Priority` is silently ignored by the broker.
- **Publisher-side `Priority`** — each ticket's logical `Severity` (Low/Normal/High/Critical) is mapped to an AMQP priority (1/3/6/9) and passed via `PublishOptions.Priority`.
- **Consumer-side reading** — `SupportTicketConsumer` logs the priority pulled from `RabbitFlowMessageContext.Priority` so you can verify the value at the receiving end.
- **Visible reordering** — `PrefetchCount = 1` + an artificial 800 ms latency per ticket build a backlog that the broker drains in priority order, even when tickets were inserted in the opposite order.

## Topology

```
                       publisher
                          │
                          │  PublishOptions.Priority = {1,3,6,9}
                          ▼
        ┌──────────────────────────────────────────┐
        │  support-tickets-queue                    │
        │  args: { x-max-priority = 9, x-dlx = ... }│
        └──────────────────┬───────────────────────┘
                           │  broker delivers highest-priority first
                           ▼
                  SupportTicketConsumer
                  (PrefetchCount=1, 800ms work)
```

## Severity → AMQP priority

| Severity | AMQP priority |
|----------|:--:|
| Critical | 9 |
| High     | 6 |
| Normal   | 3 |
| Low      | 1 |

The mapping lives in `SupportTicketsModule.MapPriority` so the wire-level number never leaks into the API payload.

## Watching priority in action

The `POST /support/tickets/burst` endpoint publishes tickets in the order
`Low → Normal → High → Critical`. Because the consumer is slow and only handles one ticket at a time, the queue accumulates and the broker drains it in the opposite order:

```text
expected log order:
  [SupportTicket] Handling … severity=Critical amqpPriority=9
  [SupportTicket] Handling … severity=Critical amqpPriority=9
  …
  [SupportTicket] Handling … severity=High amqpPriority=6
  …
  [SupportTicket] Handling … severity=Normal amqpPriority=3
  …
  [SupportTicket] Handling … severity=Low amqpPriority=1
```

## Footgun reminder

Setting `PublishOptions.Priority` on a queue that was **not** declared with `x-max-priority` is silently ignored by the broker — messages are delivered in FIFO order regardless. Keep `MaxPriority` in sync between the queue declaration and the highest value the publisher emits.

## Endpoints

| Method | Path | What it does |
|--------|------|--------------|
| POST | `/support/tickets` | Submit one ticket; its `Severity` becomes the AMQP priority. |
| POST | `/support/tickets/burst` | Publish a burst grouped by severity from lowest to highest; the consumer drains them in reverse. |

See [SupportTickets.http](SupportTickets.http) for ready-to-run requests.
