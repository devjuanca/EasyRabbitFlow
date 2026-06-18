## Configuration

All configuration is done through the `AddRabbitFlow` extension method:

```csharp
builder.Services.AddRabbitFlow(cfg =>
{
    cfg.ConfigureHost(...);                    // Connection settings
    cfg.ConfigureJsonSerializerOptions(...);   // Serialization (optional)
    cfg.ConfigurePublisher(...);               // Publisher behavior (optional)
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
| `NetworkRecoveryInterval` | TimeSpan | `10s` | Base delay for the library's recovery backoff (exponential from this value, capped at 30s) |
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

### Publisher Options

```csharp
cfg.ConfigurePublisher(pub =>
{
    pub.DisposePublisherConnection = false; // Keep connection alive (default)
});
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DisposePublisherConnection` | bool | `false` | Dispose connection after each publish |

> Every published message always carries a `MessageId`. By default it is an auto-generated GUID; pass a deterministic key via the `messageId` parameter (single) or `messageIdSelector` (batch) when you need true idempotency from business data — see [Idempotency](publishing.md#idempotency).
