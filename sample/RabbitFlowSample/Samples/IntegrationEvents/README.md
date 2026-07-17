# IntegrationEvents — Publisher-owned exchange (`DeclareExchange`)

Illustrates **`DeclareExchange`** (v8.1): a service that declares the exchange it owns directly in `AddRabbitFlow`, **without registering a single consumer**. External clients bind their own queues and routing keys to it — the publishing service neither knows nor cares who subscribes.

## What it demonstrates

- **Ownership split** — the producer owns the exchange; each consumer owns its queue and bindings. This module registers no consumers and never calls `AddConsumer`.
- **Startup declaration without consumers** — the `integration-events` topic exchange is created at application startup by the library's topology hosted service; `UseRabbitFlowConsumers()` is not required for it.
- **Publish-safety** — publishing never fails with 404 `NOT_FOUND` because the exchange always exists, even before any subscriber binds. Events with no matching binding are dropped by the broker (normal topic semantics).
- **External subscribers** — `POST /integration-events/subscriptions` simulates the *subscriber's* side (own queue + own binding pattern) with raw `RabbitMQ.Client` code, standing in for another service or an operator in the management UI.

## Topology

```
                     this service (owner)
                            │ declares at startup
                ┌───────────▼───────────┐
                │  integration-events   │  (topic)
                └───┬───────────────┬───┘
   integration.user.*       integration.#
                    │               │
      ┌─────────────▼───┐  ┌────────▼──────────────┐
      │ crm-service-    │  │ audit-service-        │
      │ users           │  │ firehose              │
      └─────────────────┘  └───────────────────────┘
        external client       external client
        (owns its queue)      (owns its queue)
```

Routing keys follow `integration.{domain}.{action}` — e.g. `integration.user.created`, `integration.booking.cancelled`.

## Configuration

```csharp
settings.DeclareExchange("integration-events", ExchangeType.Topic);
```

That's the whole producer-side setup. Durability defaults to `true`; `AutoDelete` to `false`. Extra arguments (e.g. `alternate-exchange`) go through the optional configure delegate. See [Application-Owned Exchanges](../../../../docs/configuration.md#application-owned-exchanges) for mismatch/adoption and broker-down behavior.

## Endpoints

| Method | Path | What it does |
|--------|------|--------------|
| POST | `/integration-events` | Publish an event to the owned exchange with routing key `integration.{domain}.{action}`. |
| POST | `/integration-events/subscriptions` | *Simulates an external client*: declares its own queue and binds it with its own pattern. |
| GET | `/integration-events/subscriptions/{queueName}` | Queue-state snapshot of a simulated subscriber queue, to verify routing. |

See [IntegrationEvents.http](IntegrationEvents.http) for a scripted walkthrough: two subscribers with different patterns, selective routing, and a publish with no subscribers at all.
