## Full API Reference

### Registered Services

| Interface | Lifetime | Description |
|-----------|----------|-------------|
| `IRabbitFlowPublisher` | Singleton | Publish messages to exchanges or queues |
| `IRabbitFlowState` | Singleton | Query queue metadata |
| `IRabbitFlowTemporary` | Singleton | Temporary batch processing |
| `IRabbitFlowPurger` | Singleton | Purge queue messages |
| `IRabbitFlowConsumer<TEvent>` | Transient | Your consumer implementations — resolved per message from a fresh DI scope |
| `ConsumerHostedService` | Hosted | Background consumer lifecycle (via `UseRabbitFlowConsumers`) |
| `DeadLetterReprocessorHostedService` | Hosted | [Dead-letter reprocessor](dead-letter.md#dead-letter-reprocessor) cycles (via `UseRabbitFlowConsumers`) |
| `TopologyHostedService` | Hosted | Startup declaration of [application-owned exchanges](configuration.md#application-owned-exchanges) (via `DeclareExchange`, independent of `UseRabbitFlowConsumers`) |

### Extension Methods

```csharp
// Register all EasyRabbitFlow services
IServiceCollection AddRabbitFlow(this IServiceCollection services,
    Action<RabbitFlowConfigurator> configurator = default!);

// Start background consumer processing (also starts the dead-letter reprocessor)
IServiceCollection UseRabbitFlowConsumers(this IServiceCollection services);

// Register the native health check (see Observability > Health Check)
IHealthChecksBuilder AddRabbitFlow(this IHealthChecksBuilder builder,
    string name = "rabbitflow",
    Action<RabbitFlowHealthCheckOptions>? configure = null,
    HealthStatus? failureStatus = null,
    IEnumerable<string>? tags = null);
```

### Observability Constants

| Constant | Value | Use |
|----------|-------|-----|
| `RabbitFlowDiagnostics.ActivitySourceName` | `"EasyRabbitFlow"` | `AddSource(...)` for traces |
| `RabbitFlowDiagnostics.MeterName` | `"EasyRabbitFlow"` | `AddMeter(...)` for metrics |

### RabbitFlowConfigurator Methods

| Method | Description |
|--------|-------------|
| `ConfigureHost(Action<HostSettings>)` | Set RabbitMQ connection details |
| `ConfigureJsonSerializerOptions(Action<JsonSerializerOptions>)` | Customize JSON serialization |
| `ConfigurePublisher(Action<PublisherConnectionOptions>?)` | Configure publisher behavior |
| `DeclareExchange(string exchangeName, ExchangeType exchangeType = ExchangeType.Direct, Action<ExchangeDeclaration>? configure = null)` | Declare an application-owned exchange at startup, no consumer required. Throws `RabbitFlowException` on an empty name, a name containing `deadletter`, or a duplicate declaration — see [Configuration](configuration.md#application-owned-exchanges) |
| `AddConsumer<TConsumer>(string queueName, Action<ConsumerSettings<TConsumer>>)` | Register a consumer |

### ConsumerSettings Methods

| Method | Description |
|--------|-------------|
| `ConfigureAutoGenerate(Action<AutoGenerateSettings<TConsumer>>)` | Topology to declare: exchange, dead-letter queue, [queue types](consumers.md#queue-types), replicas |
| `ConfigureDeadLetterReprocess(Action<DeadLetterReprocessSettings<TConsumer>>)` | Retry failed messages from the dead-letter queue — see [Dead-Letter Reprocessor](dead-letter.md#dead-letter-reprocessor) |
| `ConfigureRetryPolicy(Action<RetryPolicy<TConsumer>>)` | **Obsolete since v8.4.0** (removed in the next major): in-process retries. Prefer the reprocessor |

Notable properties: `Timeout` (per-attempt handler timeout, `<= 0` = none), `PrefetchCount`, `AutoAckOnError`, `ExtendDeadletterMessage`, `UnwrapDeadLetterEnvelopes`, `MaxBrokerDeliveries` (poison guard on quorum queues, default 5). See [Registering Consumers](consumers.md#registering-consumers).

### Exceptions

| Type | Thrown / used when |
|------|--------------------|
| `RabbitFlowException` | Configuration errors at registration (reserved names, invalid queue-type combinations), broker rejections surfaced by the library |
| `RabbitFlowTransientException` | Base type a handler throws to mark a failure as transient (retried by the reprocessor); derived types inherit the classification |
| `RabbitFlowHandlerAbandonedException` | Written to the dead-letter envelope when a handler ignored its cancellation token and was abandoned by the watchdog (permanent) |
| `RabbitFlowPoisonMessageException` | Written to the dead-letter envelope when the broker had returned a delivery `MaxBrokerDeliveries` times and the handler was not invoked (permanent, always parked) |
| `RabbitFlowOverRetriesException` | Legacy compatibility type; no longer thrown by the retry path |
| `RabbitFlowUnroutableMessageException` | Returned in publish result `Error` when `Mandatory = true` and the broker returns a message; includes exchange, routing key and broker reply details |

---

## Performance Notes

EasyRabbitFlow is designed for high-throughput scenarios:

- **Zero per-message reflection** — consumer handlers are compiled via expression trees at startup, not resolved per message.
- **Connection & channel reuse** — the publisher keeps a single long-lived connection by default, and single-message publishes rent confirm-channels from a bounded pool ([`MaxPooledChannels`](configuration.md#publisher-options)) instead of opening one per publish.
- **Prefetch control** — tune `PrefetchCount` for optimal throughput vs. memory usage.
- **Thread-safe channel operations** — consumer channel I/O (ACK/NACK) is serialized via a per-channel semaphore, and each publish holds exclusive use of its rented channel, preventing race conditions when `PrefetchCount > 1`.
- **Semaphore-based concurrency** — internal semaphores prevent consumer overload.
- **Library-managed recovery** — the RabbitMQ client's built-in recovery is deliberately disabled; consumers recover connection, channel, and topology themselves with exponential backoff (requires `AutomaticRecoveryEnabled = true`), and the publisher lazily re-creates its connection on the next publish.

**Recommended settings for high throughput:**

```csharp
cfg.AddConsumer<MyConsumer>("high-volume-queue", c =>
{
    c.PrefetchCount = 50;                          // Process 50 messages concurrently
    c.Timeout = TimeSpan.FromSeconds(60);          // Generous timeout for heavy processing
    c.AutoGenerate = true;
    c.ConfigureDeadLetterReprocess(r =>            // Retries come from the DLQ, never in-process
    {
        r.Enabled = true;
        r.MaxReprocessAttempts = 3;
        r.Interval = TimeSpan.FromMinutes(10);       // Minimum allowed interval
    });
});

cfg.ConfigurePublisher(pub =>
{
    pub.DisposePublisherConnection = false;         // Reuse connection (pooling is ignored when true)
    pub.MaxPooledChannels = 8;                      // Confirm-channels kept open for reuse; raise toward ~32
                                                    // (recommended ceiling, not enforced) for sustained
                                                    // high-concurrency single-message fan-outs
});
```
