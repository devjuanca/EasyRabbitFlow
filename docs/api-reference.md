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
    c.ConfigureRetryPolicy(r =>
    {
        r.MaxRetryCount = 3;
        r.RetryInterval = 500;                     // Fixed, ephemeral delay between retries
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
