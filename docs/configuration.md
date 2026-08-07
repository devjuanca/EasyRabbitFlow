## Configuration

All configuration is done through the `AddRabbitFlow` extension method:

```csharp
builder.Services.AddRabbitFlow(cfg =>
{
    cfg.ConfigureHost(...);                    // Connection settings
    cfg.ConfigureJsonSerializerOptions(...);   // Serialization (optional)
    cfg.ConfigurePublisher(...);               // Publisher behavior (optional)
    cfg.DeclareExchange(...);                  // Application-owned exchanges (optional)
    cfg.AddConsumer<T>(...);                   // Register consumers
});
```

### Host Settings

```csharp
cfg.ConfigureHost(host =>
{
    host.Host = "rabbitmq.example.com";
    host.Port = 5672;
    host.Username = "admin";
    host.Password = "secret";
    host.VirtualHost = "/";
    host.AutomaticRecoveryEnabled = true;
    host.NetworkRecoveryInterval = TimeSpan.FromSeconds(10);
    host.RequestedHeartbeat = TimeSpan.FromSeconds(30);
});
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Host` | string | `"localhost"` | RabbitMQ server hostname or IP |
| `Port` | int | `5672` | AMQP port |
| `Username` | string | `"guest"` | Authentication username |
| `Password` | string | `"guest"` | Authentication password |
| `VirtualHost` | string | `"/"` | RabbitMQ virtual host |
| `AutomaticRecoveryEnabled` | bool | `true` | Enables EasyRabbitFlow's own consumer recovery (re-connects, re-creates the channel and re-declares the full topology on an unexpected shutdown). The RabbitMQ client's built-in recovery is always disabled to avoid running two recovery systems at once. |
| `NetworkRecoveryInterval` | TimeSpan | `10s` | Base delay for the library's recovery. Consumers back off exponentially from this value, capped at 30s (or at this value when it is larger). Startup exchange declaration ([below](#application-owned-exchanges)) retries at this fixed interval (minimum 1s), without backoff. |
| `RequestedHeartbeat` | TimeSpan | `30s` | Heartbeat interval for connection health |

### JSON Serialization

Optionally customize how messages are serialized/deserialized:

```csharp
cfg.ConfigureJsonSerializerOptions(json =>
{
    json.PropertyNameCaseInsensitive = true;
    json.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    json.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
```

If not configured, EasyRabbitFlow falls back to `JsonSerializerOptions.Web` — i.e. camelCase property naming with case-insensitive deserialization, the same defaults ASP.NET Core uses for JSON. Override via `ConfigureJsonSerializerOptions` if you need a different policy.

> **Important:** the delegate receives a **fresh, empty `JsonSerializerOptions`**, not a copy of `Web`. Calling `ConfigureJsonSerializerOptions` replaces the policy entirely — if you only set, say, `DefaultIgnoreCondition`, you silently lose camelCase naming and case-insensitive matching. Configure the full policy you want (as in the example above). Only the first call is honored; subsequent calls are ignored.

### Publisher Options

```csharp
cfg.ConfigurePublisher(pub =>
{
    pub.DisposePublisherConnection = false; // Keep connection alive (default)
    pub.MaxPooledChannels = 8;              // Confirm-channels kept open for reuse (default)
});
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DisposePublisherConnection` | bool | `false` | Dispose connection after each publish |
| `PublisherId` | string | `""` | Label appended to the publisher connection name in the management UI |
| `MaxPooledChannels` | int | `8` | Confirm-channels kept open for reuse by single-message publishes. A reuse cap, not a concurrency limit: publishes beyond it open a channel on demand and dispose it on return. Size it to the expected number of concurrent single-message publishes. Ignored when `DisposePublisherConnection` is `true`. |

**Tuning `MaxPooledChannels`:** the default of 8 saturates moderate concurrency (up to ~16 concurrent
publishes). If your service performs sustained high-concurrency fan-outs of *single-message* publishes
(e.g. hundreds of `PublishAsync` calls in flight at once), raise it toward the expected concurrency — around
32 is a good ceiling: all pooled channels share one connection, so far larger values add contention instead of
throughput. The pool grows on demand up to the cap, so a higher value costs nothing until bursts actually
occur; after a burst, up to that many idle channels stay open on the publisher connection. Note that
`PublishBatchAsync` never uses the pool (each batch opens its own dedicated channel), so batch-heavy
workloads gain nothing from raising this — prefer batching itself when you control the grouping.

Pool lifecycle guarantees:

- `MaxPooledChannels` is a **strict cap** — concurrent returns never overshoot it.
- Only channels whose publish **succeeded** return to the pool. After a failure (timeout, cancellation,
  protocol error) the channel is discarded even if still open, since it may hold ambiguous confirm-tracking
  state. Under a burst of failures the pool drains and channel-open cost reappears until publishes succeed again.
- The pool is fully drained whenever the publisher connection is replaced (e.g. after a broker restart) or
  disposed — stale channels never linger across connections.

### Application-Owned Exchanges

A publisher-only service can declare the exchanges it owns without registering any consumer. This is the
standard ownership split in event-driven topologies: **the producer owns the exchange; each consumer owns its
queue and bindings**. External clients bind their own queues and routing keys to the exchange without the
publishing service knowing about them.

```csharp
cfg.DeclareExchange("orders-events", ExchangeType.Topic);

cfg.DeclareExchange("billing-events", ExchangeType.Fanout, exchange =>
{
    exchange.Durable = true;        // default
    exchange.AutoDelete = false;    // default
    exchange.Args = new Dictionary<string, object?>
    {
        ["alternate-exchange"] = "billing-events-unrouted"
    };
});
```

Call `DeclareExchange` once per exchange — as many as the service owns. The exchanges are created at
application startup by a dedicated hosted service; `UseRabbitFlowConsumers()` is **not** required.

| Parameter / Property | Type | Default | Description |
|----------------------|------|---------|-------------|
| `exchangeName` | string | — | Name of the exchange. Must be non-empty; names containing `deadletter` are reserved for framework-generated dead-letter topology. |
| `exchangeType` | ExchangeType | `Direct` | Routing semantics: `Direct`, `Fanout`, `Topic`, or `Headers`. |
| `Durable` | bool | `true` | Whether the exchange survives broker restarts. |
| `AutoDelete` | bool | `false` | Whether the exchange is deleted when its last binding is removed. Rarely wanted for a publisher-owned exchange. |
| `Args` | IDictionary | `null` | Optional exchange arguments (e.g. `alternate-exchange`). |

Behavior notes:

- **Idempotent** — an exchange that already exists with identical settings is a no-op.
- **Mismatch-tolerant** — if the exchange already exists with a different type or arguments, the existing
  exchange is adopted and a warning is logged instead of failing the application start. To apply the new
  settings, delete the exchange on the broker (draining bound queues first) and restart.
- **Broker down at startup** — the host still starts. When `AutomaticRecoveryEnabled` is `true` (default),
  declaration keeps retrying in the background at a fixed `NetworkRecoveryInterval` (minimum 1s, no backoff)
  until it succeeds; when it is `false`, exchanges stay undeclared until the application restarts. Publishes
  to a not-yet-declared exchange fail and surface through `PublishResult.Success` / `Error` in the meantime.
- Invalid declarations throw `RabbitFlowException` at configuration time: an empty name, a name containing
  `deadletter`, or declaring the same name twice (comparison is case-sensitive).
- `DeclareExchange` requires `ConfigureHost` — the startup declaration service needs the connection settings.

> **`ExchangeType` name collision:** `EasyRabbitFlow.Settings.ExchangeType` is an enum that shares its name
> with the static `RabbitMQ.Client.ExchangeType` class. In a file that imports both namespaces, disambiguate
> with an alias: `using ExchangeType = EasyRabbitFlow.Settings.ExchangeType;`

> Every published message always carries a `MessageId`. By default it is an auto-generated GUID; pass a deterministic key via the `messageId` parameter (single) or `messageIdSelector` (batch) when you need true idempotency from business data — see [Idempotency](publishing.md#idempotency).
