<p align="center">
  <img src="icon.png" alt="EasyRabbitFlow" width="120" />
</p>

<h1 align="center">EasyRabbitFlow</h1>

<p align="center">
  <strong>High-performance RabbitMQ client for .NET — fluent configuration, automatic topology, zero-ceremony consumers.</strong>
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/EasyRabbitFlow"><img src="https://img.shields.io/nuget/v/EasyRabbitFlow" alt="NuGet" /></a>
  <a href="https://www.nuget.org/packages/EasyRabbitFlow"><img src="https://img.shields.io/nuget/dt/EasyRabbitFlow" alt="Downloads" /></a>
  <a href="LICENSE.txt"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="License" /></a>
</p>

---

## Table of Contents

- [Why EasyRabbitFlow?](#why-easyrabbitflow)
- [Architecture Overview](#architecture-overview)
- [Getting Started](#getting-started)
  - [Installation](#installation)
  - [Quick Start](#quick-start)
- [Configuration](#configuration)
  - [Host Settings](#host-settings)
  - [JSON Serialization](#json-serialization)
  - [Publisher Options](#publisher-options)
- [Consumers](#consumers)
  - [Implementing a Consumer](#implementing-a-consumer)
  - [Registering Consumers](#registering-consumers)
  - [Auto-Generate Topology](#auto-generate-topology)
  - [Retry Policies](#retry-policies)
  - [Custom Dead-Letter Queues](#custom-dead-letter-queues)
- [Publishing Messages](#publishing-messages)
- [Queue State Inspection](#queue-state-inspection)
- [Queue Purging](#queue-purging)
- [Temporary Batch Processing](#temporary-batch-processing)
- [Transient Exceptions & Custom Retry Logic](#transient-exceptions--custom-retry-logic)
- [Full API Reference](#full-api-reference)
- [Performance Notes](#performance-notes)

---

## Why EasyRabbitFlow?

| Feature | EasyRabbitFlow |
|---------|----------------|
| Fluent, strongly-typed configuration | ✅ |
| Automatic queue / exchange / dead-letter generation | ✅ |
| Reflection-free per-message processing | ✅ |
| Configurable retry with exponential backoff | ✅ |
| Temporary batch processing with auto-cleanup | ✅ |
| Queue state & purge utilities | ✅ |
| Full DI integration (scoped/transient/singleton) | ✅ |
| Transactional or confirm-mode publishing | ✅ |
| .NET Standard 2.1 (works with .NET 6, 7, 8, 9+) | ✅ |

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                         Your Application                            │
├────────────┬──────────────┬──────────────┬──────────────────────────┤
│ Publisher  │  Consumers   │   State /    │  Temporary Batch         │
│            │  (Hosted)    │   Purger     │  Processing              │
├────────────┴──────────────┴──────────────┴──────────────────────────┤
│                     EasyRabbitFlow Library                           │
│                                                                     │
│  ┌──────────────┐  ┌────────────────┐  ┌─────────────────────────┐ │
│  │ IRabbitFlow   │  │ ConsumerHosted │  │ IRabbitFlowTemporary    │ │
│  │ Publisher     │  │ Service        │  │ (batch processing)      │ │
│  └──────┬───────┘  └───────┬────────┘  └────────────┬────────────┘ │
│         │                  │                         │              │
│  ┌──────┴──────────────────┴─────────────────────────┴────────┐    │
│  │              RabbitMQ.Client (ConnectionFactory)            │    │
│  └────────────────────────────┬────────────────────────────────┘    │
└───────────────────────────────┼─────────────────────────────────────┘
                                │
                    ┌───────────┴───────────┐
                    │     RabbitMQ Broker    │
                    │                       │
                    │  ┌─────────────────┐  │
                    │  │   Exchanges     │  │
                    │  └────────┬────────┘  │
                    │           │            │
                    │  ┌────────▼────────┐  │
                    │  │     Queues      │  │
                    │  └────────┬────────┘  │
                    │           │            │
                    │  ┌────────▼────────┐  │
                    │  │  Dead-Letter    │  │
                    │  │    Queues       │  │
                    │  └─────────────────┘  │
                    └───────────────────────┘
```

### Message Flow

```
  Publisher                    RabbitMQ                        Consumer
  ────────                    ────────                        ────────

  PublishAsync() ──────►  Exchange ──routing──► Queue ──────► HandleAsync()
                                                  │               │
                                                  │          (on failure)
                                                  │               │
                                                  │          Retry Policy
                                                  │          (exponential
                                                  │           backoff)
                                                  │               │
                                                  │          (exhausted)
                                                  │               │
                                                  └──► Dead-Letter Queue
```

---

## Getting Started

### Installation

```bash
dotnet add package EasyRabbitFlow
```

### Quick Start

**1. Define your event model:**

```csharp
public class OrderCreatedEvent
{
    public string OrderId { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

**2. Implement a consumer:**

```csharp
public class OrderConsumer : IRabbitFlowConsumer<OrderCreatedEvent>
{
    private readonly ILogger<OrderConsumer> _logger;

    public OrderConsumer(ILogger<OrderConsumer> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(OrderCreatedEvent message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing order {OrderId}, total: {Total}",
            message.OrderId, message.Total);

        // Your business logic here: save to DB, send email, call API, etc.
        await Task.CompletedTask;
    }
}
```

**3. Configure in `Program.cs`:**

```csharp
builder.Services
    .AddRabbitFlow(cfg =>
    {
        cfg.ConfigureHost(host =>
        {
            host.Host = "localhost";
            host.Port = 5672;
            host.Username = "guest";
            host.Password = "guest";
        });

        cfg.AddConsumer<OrderConsumer>("orders-queue", c =>
        {
            c.AutoGenerate = true;
            c.PrefetchCount = 10;
            c.Timeout = TimeSpan.FromSeconds(30);
            c.ConfigureRetryPolicy(r =>
            {
                r.MaxRetryCount = 3;
                r.ExponentialBackoff = true;
                r.ExponentialBackoffFactor = 2;
            });
        });
    })
    .UseRabbitFlowConsumers(); // Starts background consumer automatically
```

**4. Publish from an endpoint or service:**

```csharp
app.MapPost("/orders", async (OrderCreatedEvent order, IRabbitFlowPublisher publisher) =>
{
    var success = await publisher.PublishAsync(order, "orders-queue");
    return success ? Results.Ok("Order queued") : Results.Problem("Failed to queue order");
});
```

That's it — **four steps** from zero to a working publish/consume pipeline.

---

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
| `AutomaticRecoveryEnabled` | bool | `true` | Auto-reconnect after failures |
| `TopologyRecoveryEnabled` | bool | `true` | Auto-recover queues/exchanges after reconnect |
| `NetworkRecoveryInterval` | TimeSpan | `10s` | Wait time between recovery attempts |
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

If not configured, the default `JsonSerializerOptions` are used.

### Publisher Options

```csharp
cfg.ConfigurePublisher(pub =>
{
    pub.DisposePublisherConnection = false; // Keep connection alive (default)
    pub.ChannelMode = ChannelMode.Confirm;  // Confirm mode (default)
});
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DisposePublisherConnection` | bool | `false` | Dispose connection after each publish |
| `ChannelMode` | ChannelMode | `Confirm` | `Confirm` (fast + reliable) or `Transactional` (full ACID) |

---

## Consumers

### Implementing a Consumer

Every consumer implements `IRabbitFlowConsumer<TEvent>`:

```csharp
public interface IRabbitFlowConsumer<TEvent>
{
    Task HandleAsync(TEvent message, CancellationToken cancellationToken);
}
```

Consumers support **full dependency injection** — you can inject any service (scoped, transient, singleton):

```csharp
public class EmailConsumer : IRabbitFlowConsumer<NotificationEvent>
{
    private readonly IEmailService _emailService;
    private readonly ILogger<EmailConsumer> _logger;

    public EmailConsumer(IEmailService emailService, ILogger<EmailConsumer> logger)
    {
        _emailService = emailService;
        _logger = logger;
    }

    public async Task HandleAsync(NotificationEvent message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Sending email to {Recipient}", message.Email);
        await _emailService.SendAsync(message.Email, message.Subject, message.Body, cancellationToken);
    }
}
```

### Registering Consumers

```csharp
cfg.AddConsumer<EmailConsumer>("email-queue", c =>
{
    c.Enable = true;                              // Enable/disable this consumer
    c.PrefetchCount = 5;                          // Messages fetched in parallel
    c.Timeout = TimeSpan.FromSeconds(30);         // Per-message processing timeout
    c.AutoAckOnError = false;                     // Don't ack failed messages
    c.ExtendDeadletterMessage = true;             // Add error details to DLQ messages
    c.ConsumerId = "email-consumer-1";            // Custom connection identifier
});
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enable` | bool | `true` | Whether this consumer is active |
| `QueueName` | string | *(set in constructor)* | Queue to consume from |
| `ConsumerId` | string? | `null` | Custom connection ID (falls back to queue name) |
| `PrefetchCount` | ushort | `1` | How many messages to prefetch |
| `Timeout` | TimeSpan | `30s` | Processing timeout per message |
| `AutoAckOnError` | bool | `false` | Auto-acknowledge on error (message lost) |
| `AutoGenerate` | bool | `false` | Auto-create queue/exchange/DLQ |
| `ExtendDeadletterMessage` | bool | `false` | Enrich dead-letter messages with error details |

### Auto-Generate Topology

When `AutoGenerate = true`, EasyRabbitFlow creates queues, exchanges, and dead-letter queues automatically:

```csharp
cfg.AddConsumer<OrderConsumer>("orders-queue", c =>
{
    c.AutoGenerate = true;
    c.ConfigureAutoGenerate(ag =>
    {
        ag.ExchangeName = "orders-exchange";      // Custom exchange name
        ag.ExchangeType = ExchangeType.Fanout;    // Direct | Fanout | Topic | Headers
        ag.RoutingKey = "orders-routing-key";      // Routing key for binding
        ag.GenerateExchange = true;                // Create the exchange
        ag.GenerateDeadletterQueue = true;         // Create a dead-letter queue
        ag.DurableQueue = true;                    // Queue survives broker restart
        ag.DurableExchange = true;                 // Exchange survives broker restart
        ag.ExclusiveQueue = false;                 // Not limited to one connection
        ag.AutoDeleteQueue = false;                // Don't delete when last consumer disconnects
    });
});
```

**Generated topology when `AutoGenerate = true`:**

```
                        ┌──────────────────────┐
                        │  orders-exchange      │
                        │  (fanout)             │
                        └──────────┬───────────┘
                                   │ routing-key
                        ┌──────────▼───────────┐
                        │  orders-queue         │──── args: x-dead-letter-exchange
                        │  (durable)            │         x-dead-letter-routing-key
                        └──────────┬───────────┘
                                   │ (on failure)
                  ┌────────────────▼─────────────────┐
                  │  orders-queue-deadletter-exchange │
                  │  (direct)                         │
                  └────────────────┬─────────────────┘
                                   │
                  ┌────────────────▼─────────────────┐
                  │  orders-queue-deadletter          │
                  └──────────────────────────────────┘
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `GenerateExchange` | bool | `true` | Create the exchange |
| `GenerateDeadletterQueue` | bool | `true` | Create dead-letter queue and exchange |
| `ExchangeType` | ExchangeType | `Direct` | `Direct`, `Fanout`, `Topic`, or `Headers` |
| `ExchangeName` | string? | `null` | Custom name (defaults to `{queue}-exchange`) |
| `RoutingKey` | string? | `null` | Custom routing key (defaults to `{queue}-routing-key`) |
| `DurableExchange` | bool | `true` | Exchange survives broker restart |
| `DurableQueue` | bool | `true` | Queue survives broker restart |
| `ExclusiveQueue` | bool | `false` | Queue limited to declaring connection |
| `AutoDeleteQueue` | bool | `false` | Delete queue when last consumer disconnects |
| `Args` | IDictionary? | `null` | Additional RabbitMQ arguments |

### Retry Policies

Configure how failed messages are retried:

```csharp
c.ConfigureRetryPolicy(r =>
{
    r.MaxRetryCount = 5;               // Number of attempts
    r.RetryInterval = 1000;            // Base delay in ms
    r.ExponentialBackoff = true;        // Enable exponential backoff
    r.ExponentialBackoffFactor = 2;     // Multiply delay by this factor
    r.MaxRetryDelay = 30_000;           // Cap delay at 30 seconds
});
```

**Example: retry timeline with exponential backoff (factor=2, base=1000ms):**

```
Attempt 1 → fail → wait 1000ms
Attempt 2 → fail → wait 2000ms
Attempt 3 → fail → wait 4000ms
Attempt 4 → fail → wait 8000ms
Attempt 5 → fail → sent to dead-letter queue
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxRetryCount` | int | `1` | Total retry attempts |
| `RetryInterval` | int | `1000` | Base delay between retries (ms) |
| `ExponentialBackoff` | bool | `false` | Enable exponential backoff |
| `ExponentialBackoffFactor` | int | `1` | Multiplier for exponential growth |
| `MaxRetryDelay` | int | `60000` | Upper bound for delay (ms) |

### Custom Dead-Letter Queues

Route failed messages to a specific dead-letter queue:

```csharp
c.ConfigureCustomDeadletter(dl =>
{
    dl.DeadletterQueueName = "custom-errors-queue";
});
```

When `ExtendDeadletterMessage = true`, the dead-letter message includes full error details:

```json
{
  "dateUtc": "2026-01-15T10:30:00Z",
  "messageType": "OrderCreatedEvent",
  "messageData": { "orderId": "ORD-123", "total": 99.99 },
  "exceptionType": "TimeoutException",
  "errorMessage": "The operation was canceled.",
  "stackTrace": "...",
  "source": "OrderService",
  "innerExceptions": []
}
```

---

## Publishing Messages

Inject `IRabbitFlowPublisher` and publish to exchanges or queues:

```csharp
public class OrderService
{
    private readonly IRabbitFlowPublisher _publisher;

    public OrderService(IRabbitFlowPublisher publisher)
    {
        _publisher = publisher;
    }

    // Publish directly to a queue
    public async Task<bool> CreateOrderAsync(OrderCreatedEvent order)
    {
        return await _publisher.PublishAsync(order, queueName: "orders-queue");
    }

    // Publish to an exchange with routing key
    public async Task<bool> BroadcastNotificationAsync(NotificationEvent notification)
    {
        return await _publisher.PublishAsync(
            notification,
            exchangeName: "notifications",
            routingKey: "user.created");
    }
}
```

**Publisher method signatures:**

```csharp
// Publish to exchange (with routing key)
Task<bool> PublishAsync<TEvent>(TEvent message, string exchangeName, string routingKey,
    string publisherId = "", JsonSerializerOptions? jsonOptions = null,
    CancellationToken cancellationToken = default) where TEvent : class;

// Publish directly to queue
Task<bool> PublishAsync<TEvent>(TEvent message, string queueName,
    string publisherId = "", JsonSerializerOptions? jsonOptions = null,
    CancellationToken cancellationToken = default) where TEvent : class;
```

Returns `true` on success, `false` on failure (errors are logged internally).

---

## Queue State Inspection

Use `IRabbitFlowState` to query queue metadata at runtime:

```csharp
public class HealthCheckService
{
    private readonly IRabbitFlowState _state;

    public HealthCheckService(IRabbitFlowState state)
    {
        _state = state;
    }

    public async Task<object> GetQueueHealthAsync(string queueName)
    {
        return new
        {
            IsEmpty = await _state.IsEmptyQueueAsync(queueName),
            MessageCount = await _state.GetQueueLengthAsync(queueName),
            ConsumerCount = await _state.GetConsumersCountAsync(queueName),
            HasConsumers = await _state.HasConsumersAsync(queueName)
        };
    }
}
```

| Method | Returns | Description |
|--------|---------|-------------|
| `IsEmptyQueueAsync(queueName)` | `Task<bool>` | Is the queue empty? |
| `GetQueueLengthAsync(queueName)` | `Task<uint>` | Number of messages in the queue |
| `GetConsumersCountAsync(queueName)` | `Task<uint>` | Number of active consumers |
| `HasConsumersAsync(queueName)` | `Task<bool>` | Does the queue have any consumers? |

---

## Queue Purging

Use `IRabbitFlowPurger` to remove all messages from queues:

```csharp
// Purge a single queue
await purger.PurgeMessagesAsync("orders-queue");

// Purge multiple queues at once
await purger.PurgeMessagesAsync(new[] { "orders-queue", "emails-queue", "notifications-queue" });
```

---

## Temporary Batch Processing

`IRabbitFlowTemporary` is designed for **fire-and-forget batch workflows** — process a collection of messages through RabbitMQ with automatic queue creation and cleanup.

**Ideal for:**
- Background jobs (PDF generation, email sending, report calculation)
- One-time batch processing (database cleanup, data migration)
- Parallel processing with configurable concurrency

### Basic Usage

```csharp
public class InvoiceService
{
    private readonly IRabbitFlowTemporary _temporary;

    public InvoiceService(IRabbitFlowTemporary temporary)
    {
        _temporary = temporary;
    }

    public async Task ProcessInvoiceBatchAsync(List<Invoice> invoices)
    {
        int processed = await _temporary.RunAsync(
            invoices,
            onMessageReceived: async (invoice, ct) =>
            {
                Console.WriteLine($"Processing invoice {invoice.Id}...");
                await Task.Delay(500, ct); // Simulate work
            },
            onCompleted: (total, errors) =>
            {
                Console.WriteLine($"Done! Processed: {total}, Errors: {errors}");
            },
            options: new RunTemporaryOptions
            {
                PrefetchCount = 10,
                Timeout = TimeSpan.FromSeconds(30),
                CorrelationId = Guid.NewGuid().ToString()
            });
    }
}
```

### With Result Collection

```csharp
int processed = await _temporary.RunAsync<Invoice, InvoiceResult>(
    invoices,
    onMessageReceived: async (invoice, ct) =>
    {
        var result = await ProcessInvoiceAsync(invoice, ct);
        return new InvoiceResult { InvoiceId = invoice.Id, Status = "Completed" };
    },
    onCompletedAsync: async (count, results) =>
    {
        // results is a ConcurrentQueue<InvoiceResult> with all collected results
        Console.WriteLine($"Processed {count} invoices, collected {results.Count} results");
        await SaveResultsAsync(results);
    });
```

### How It Works

```
  Your Code                   RabbitMQ (Temporary)            Handler
  ─────────                   ──────────────────              ───────

  RunAsync(messages) ─────►  Create temp queue  ───────────►  onMessageReceived()
       │                     Publish all msgs                      │
       │                           │                               │
       │                     Consume & process  ◄──────────────────┘
       │                           │
       │                     All processed?
       │                           │ yes
       ◄──────────────────── Delete temp queue
       │                     Call onCompleted()
       │
  return count
```

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `PrefetchCount` | ushort | `1` | Parallel message processing (>0) |
| `Timeout` | TimeSpan? | `null` | Per-message timeout |
| `QueuePrefixName` | string? | `null` | Custom prefix for the temp queue name |
| `CorrelationId` | string? | `Guid` | Correlation ID for tracing/logging |

---

## Transient Exceptions & Custom Retry Logic

By default, timeouts and `RabbitFlowTransientException` trigger retries. Throw `RabbitFlowTransientException` from your consumer to signal a retryable error:

```csharp
using EasyRabbitFlow.Exceptions;

public class PaymentConsumer : IRabbitFlowConsumer<PaymentEvent>
{
    public async Task HandleAsync(PaymentEvent message, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessPaymentAsync(message);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            // This will trigger the retry policy
            throw new RabbitFlowTransientException("Payment gateway temporarily unavailable", ex);
        }
        // Any other exception → no retry, sent to dead-letter queue
    }
}
```

**Exception types:**

| Exception | Purpose |
|-----------|---------|
| `RabbitFlowTransientException` | Signals a retryable error — triggers retry policy |
| `RabbitFlowException` | General library error |
| `RabbitFlowOverRetriesException` | Thrown internally when all retry attempts are exhausted |

---

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
```

### RabbitFlowConfigurator Methods

| Method | Description |
|--------|-------------|
| `ConfigureHost(Action<HostSettings>)` | Set RabbitMQ connection details |
| `ConfigureJsonSerializerOptions(Action<JsonSerializerOptions>)` | Customize JSON serialization |
| `ConfigurePublisher(Action<PublisherOptions>?)` | Configure publisher behavior |
| `AddConsumer<TConsumer>(string queueName, Action<ConsumerSettings<TConsumer>>)` | Register a consumer |

---

## Performance Notes

EasyRabbitFlow is designed for high-throughput scenarios:

- **Zero per-message reflection** — consumer handlers are compiled via expression trees at startup, not resolved per message.
- **Connection pooling** — publisher reuses a single connection by default.
- **Prefetch control** — tune `PrefetchCount` for optimal throughput vs. memory usage.
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
        r.RetryInterval = 500;
        r.ExponentialBackoff = true;
        r.ExponentialBackoffFactor = 2;
        r.MaxRetryDelay = 10_000;                  // Cap at 10 seconds
    });
});

cfg.ConfigurePublisher(pub =>
{
    pub.DisposePublisherConnection = false;         // Reuse connection
    pub.ChannelMode = ChannelMode.Confirm;          // Fast + reliable
});
```

---

## License

This project is licensed under the [MIT License](LICENSE.txt).
