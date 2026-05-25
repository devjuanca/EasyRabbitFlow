# Orders — Topic exchange routing

Illustrates the **topic exchange** pattern: one exchange, many consumers, each binding to a different pattern of the routing key. The broker dispatches each published message only to the queues whose binding matches.

## What it demonstrates

- **Topic routing** — routing key shape `orders.{region}.{status}`.
- **Pattern wildcards**:
  - `*` matches exactly one dotted word.
  - `#` matches zero or more dotted words.
- **Same exchange, different bindings**:
  - `EuOrdersConsumer` binds `orders.eu.*` — every status, EU only.
  - `OrderCreatedConsumer` binds `orders.*.created` — only "created", any region.
  - `OrderAuditConsumer` binds `orders.#` — catch-all audit feed.

## Topology

```
                ┌────────────────────────┐
                │  orders-topic (topic)  │
                └────────────┬───────────┘
       ┌────────────┬────────┴────────┬──────────────┐
       │            │                 │              │
orders.eu.*   orders.*.created    orders.#       (other bindings ignored)
       │            │                 │
┌──────▼─────┐ ┌────▼───────────┐ ┌──▼─────────────┐
│orders-eu-q │ │orders-created-q│ │orders-audit-q  │
└──────┬─────┘ └────┬───────────┘ └──┬─────────────┘
EuOrders        OrderCreated      OrderAudit
```

## Routing-key matrix

| Published key | EU (`orders.eu.*`) | Created (`orders.*.created`) | Audit (`orders.#`) |
|---------------|:--:|:--:|:--:|
| `orders.eu.created` | ✓ | ✓ | ✓ |
| `orders.eu.shipped` | ✓ |   | ✓ |
| `orders.us.created` |   | ✓ | ✓ |
| `orders.ap.cancelled` |   |   | ✓ |

## Endpoints

| Method | Path | What it does |
|--------|------|--------------|
| POST | `/orders/{region}/{status}` | Publish one OrderEvent with routing key `orders.{region}.{status}`. |
| POST | `/orders/random/{count}` | Publish a random burst across regions/statuses; watch the consumers fire at different rates. |

See [Orders.http](Orders.http) for ready-to-run requests.
