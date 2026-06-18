## Full API Reference

### Registered Services

| Interface | Lifetime | Description |
|-----------|----------|-------------|
| `IRabbitFlowPublisher` | Singleton | Publish messages to exchanges or queues |
| `IRabbitFlowState` | Singleton | Query queue metadata |
| `IRabbitFlowTemporary` | Singleton | Temporary batch processing |
| `IRabbitFlowPurger` | Singleton | Purge queue messages |
| `ConsumerHostedService` | Hosted | Background consumer lifecycle (via `UseRabbitFlowConsumers`) |

### Extension Methods

```csharp
// Register all EasyRabbitFlow services
IServiceCollection AddRabbitFlow(this IServiceCollection services,
    Action<RabbitFlowConfigurator>? configurator = null);

// Start background consumer processing
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
| `AddConsumer<TConsumer>(string queueName, Action<ConsumerSettings<TConsumer>>)` | Register a consumer |

---

## Performance Notes

EasyRabbitFlow is designed for high-throughput scenarios:

- **Zero per-message reflection** — consumer handlers are compiled via expression trees at startup, not resolved per message.
- **Connection pooling** — publisher reuses a single connection by default.
- **Prefetch control** — tune `PrefetchCount` for optimal throughput vs. memory usage.
- **Thread-safe channel operations** — all channel I/O (ACK/NACK/Publish) is serialized via per-channel semaphores, preventing race conditions when `PrefetchCount > 1`.
- **Semaphore-based concurrency** — internal semaphores prevent consumer overload.
- **Automatic recovery** — connections auto-recover after network failures with configurable intervals.

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
    pub.DisposePublisherConnection = false;         // Reuse connection
});
```
