## RabbitFlow Documentation

Welcome to **RabbitFlow**, a streamlined .NET library for configuring RabbitMQ messaging with minimal ceremony and high performance.

### Table of Contents

1. [Introduction](#introduction)
2. [Install](#install)
3. [Configuration](#configuration)
   - [Host Configuration](#host-configuration)
   - [JSON Serialization Options](#json-serialization-options)
   - [Publisher Options](#publisher-options)
4. [Consumers](#consumers)
   - [Adding Consumers](#adding-consumers)
   - [Auto-Generate Queue / Exchange](#auto-generate-queue-exchange)
   - [Custom Dead-Letter](#custom-dead-letter)
   - [Retry Policies](#retry-policies)
   - [Consumer Interface Implementation](#consumer-interface-implementation)
5. [Hosted Initialization (Recommended)](#hosted-initialization-recommended)
6. [Publishing Messages](#publishing-messages)
7. [Queue State](#queue-state)
8. [Temporary Queue Processing](#temporary-message-processing)
9. [Performance Notes](#performance-notes)


### 1. Introduction

RabbitFlow simplifies integration with RabbitMQ by:
- Auto-registering consumers with strongly typed settings.
- Optional automatic queue/exchange/dead-letter generation.
- Efficient retry, timeout, and error handling.
- High performance message processing (no per-message reflection).

It supports both pre-existing infrastructure and on-demand generation.

Let’s explore setup and usage.
### 2. Install

To install the **RabbitFlow** library into your project, you can use the NuGet package manager:

```bash
dotnet add package EasyRabbitFlow
```

### 3. Configuration
Register core services using `AddRabbitFlow`, then optionally start the hosted consumer service with `UseRabbitFlowConsumers`.
```csharp
builder.Services
    .AddRabbitFlow(cfg =>
    {
        cfg.ConfigureHost(host =>
        {
            host.Host = "rabbitmq.example.com";
            host.Username = "guest";
            host.Password = "guest";
        });

        cfg.ConfigureJsonSerializerOptions(json =>
        {
            json.PropertyNameCaseInsensitive = true;
            json.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            json.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        cfg.ConfigurePublisher(pub => pub.DisposePublisherConnection = false);

        cfg.AddConsumer<EmailConsumer>("email-queue", c =>
        {
            c.PrefetchCount = 5;
            c.Timeout = TimeSpan.FromSeconds(2);
            c.AutoGenerate = true;
            c.ConfigureAutoGenerate(a =>
            {
                a.ExchangeName = "notifications";
                a.ExchangeType = ExchangeType.Fanout;
                a.GenerateDeadletterQueue = true;
            });
            c.ConfigureRetryPolicy(r =>
            {
                r.MaxRetryCount = 3;
                r.RetryInterval = 1000; // ms
                r.ExponentialBackoff = true;
                r.ExponentialBackoffFactor = 2;
            });
        });
    })
    .UseRabbitFlowConsumers(); // starts background consumption
```

- #### 3.1 Host Configuration
The ConfigureHost method allows you to specify the connection details for your RabbitMQ host:
```csharp
opt.ConfigureHost(hostSettings =>
{
    hostSettings.Host = "rabbitmq.example.com";
    hostSettings.Username = "guest";
    hostSettings.Password = "guest";
});
```

- #### 3.2 JSON Serialization Options
This option allows you to globally configure how JSON serialization should be handled. This configuration is optional; if not provided, the default JsonSerializerOptions will be used.
```csharp
opt.ConfigureJsonSerializerOptions(jsonSettings =>
{
    jsonSettings.PropertyNameCaseInsensitive = true;
    jsonSettings.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    jsonSettings.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
```


- #### 3.3 Publisher Options
Optionally, you may configure the publisher that you intend to use by defining the 'DisposePublisherConnection' variable. 

This variable determines whether the connection established by the publisher with RabbitMQ should be kept alive or terminated upon the completion of the process.
The default value is false.
```csharp
 opt.ConfigurePublisher(publisherSettings => publisherSettings.DisposePublisherConnection = true);
```

### 4. Consumers
- #### 4.1 Adding Consumers
Define and configure consumers for specific queues using the AddConsumer method:

All consumers must implement the interface `IRabbitFlowConsumer<TEvent>` where TEvent us the event or message model.

Using the `AddConsumer` method, all required services and configurations will be created and registered into the DI container, ready for use.

```csharp
opt.AddConsumer<EmailConsumer>("email-queue", consumerSettings =>
{
    consumerSettings.PrefetchCount = 1;
    consumerSettings.Timeout = TimeSpan.FromMilliseconds(500);
    consumerSettings.AutoGenerate = true;
    consumerSettings.ConfigureAutoGenerate(opt =>
      {
     	opt.DurableQueue = true;
     	opt.DurableExchange = true;
     	opt.ExclusiveQueue = false;
     	opt.AutoDeleteQueue = false;
     	opt.GenerateDeadletterQueue = true;
     	opt.ExchangeType = ExchangeType.Direct;
    	// ... other settings ...
      });
```

#### 4.2 Retry Policies
You can configure a retry policy to handle message processing failures effectively. 

By default, all exceptions related to timeout issues will be automatically retried if the retry mechanism is enabled.

Additionally, you can customize the retry logic by defining your own rules for handling specific use cases using the `TranscientException`  class from the `EasyRabbitFlow.Exceptions` namespace.

```csharp
consumerSettings.ConfigureRetryPolicy(retryPolicy =>
{
    retryPolicy.MaxRetryCount = 3;
    retryPolicy.RetryInterval = 1000;
    retryPolicy.ExponentialBackoff = true;
    retryPolicy.ExponentialBackoffFactor = 2;
});
```

#### 4.3 Consumer Interface Implementation

Consumers must implement the `IRabbitFlowConsumer<TEvent>` interface:

```csharp
// Consumers must implement the IRabbitFlowConsumer<TEvent> interface:

public interface IRabbitFlowConsumer<TEvent>
{
    Task HandleAsync(TEvent message, CancellationToken cancellationToken);
}

// Example EmailConsumer

public class EmailConsumer : IRabbitFlowConsumer<EmailEvent>
{
    private readonly ILogger<EmailConsumer> _logger;

    public EmailConsumer(ILogger<EmailConsumer> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(EmailEvent message, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        _logger.LogInformation("New email event received. Event:{event}", JsonSerializer.Serialize(message));
    }
}


```


### 5. Hosted Initialization (Recommended)
Consumers are automatically started by calling:
```csharp
builder.Services.AddRabbitFlow(cfg => { /* config */ })
                .UseRabbitFlowConsumers();
```
Older manual initialization methods are deprecated.

### 6. Publishing Messages
Publisher Interface

Use the IRabbitFlowPublisher interface to publish messages to a RabbitMQ:

`JsonSerializerOptions` can be overridden from global settings.

The `publisherId` parameter is intended to identify the connection created with RabbitMQ.

```csharp
public interface IRabbitFlowPublisher
{
    Task<bool> PublishAsync<TEvent>(TEvent message, string exchangeName, string routingKey, string publisherId = "", JsonSerializerOptions? jsonOptions = null) where TEvent : class;

    Task<bool> PublishAsync<TEvent>(TEvent message, string queueName, string publisherId = "", JsonSerializerOptions? jsonOptions = null) where TEvent : class;
}

```

### 7. Queue State
The `IRabbitFlowState` interface allows you to access queue status information:

```csharp
public interface IRabbitFlowState
{
    bool IsEmptyQueue(string queueName);
    uint GetQueueLength(string queueName);
    uint GetConsumersCount(string queueName);
    bool QueueHasConsumers(string queueName);
}

```

### 8. Temporary Message Processing

`IRabbitFlowTemporary` is a utility designed to simplify **fire-and-forget style workflows** where a batch of messages is sent to RabbitMQ, processed by handlers, and discarded — all within a **temporary queue**.

This is ideal for:
- **Background jobs** that need to process large collections of data (e.g., generating PDFs, sending emails, calculating reports), where you want to **free up the main thread or HTTP request quickly** and continue processing in the background.
- **Ephemeral tasks** that don't need long-term queues or persistent consumers.
- **One-time batch processing**, such as database cleanup, syncing remote systems, or running onboarding flows.

This approach lets you **publish the work and return immediately**, while the internal RabbitMQ mechanism ensures each message is processed asynchronously and independently — with timeout and cancellation support.

---

### ✨ How It Works

- A **temporary, exclusive queue** and exchange are created automatically for the message type.
- All messages are published to this queue and immediately consumed by an internal async handler.
- The temporary queue and exchange are deleted automatically after the process completes.
- You can specify:
  - Per-message timeout.
  - Degree of parallelism via prefetch.
  - A callback on completion.
  - A global cancellation token.

---

### 🔧 API Overview

```csharp
Task<int> RunAsync<T>(
    IReadOnlyList<T> messages,
    Func<T, CancellationToken, Task> onMessageReceived,
    Action<int, int>? onCompleted = null,
    RunTemporaryOptions? options = null,
    CancellationToken cancellationToken = default
) where T : class;
```

Sample
```csharp
public record InvoiceToProcess(string InvoiceId, decimal Amount);

public class InvoiceService
{
    private readonly IRabbitFlowTemporary _rabbitFlow;

    public InvoiceService(IRabbitFlowTemporary rabbitFlow)
    {
        _rabbitFlow = rabbitFlow;
    }

    public async Task StartBatchProcessingAsync(List<InvoiceToProcess> invoices)
    {
        _ = _rabbitFlow.RunAsync(
            invoices,
            async (invoice, ct) =>
            {
                Console.WriteLine($"Processing invoice {invoice.InvoiceId}...");

                // Simulate long-running processing
                await Task.Delay(1000, ct);

                // You could do: Save to DB, call APIs, generate PDFs, etc.
                Console.WriteLine($"Finished invoice {invoice.InvoiceId}");
            },
            onCompleted: (success, errors) =>
            {
                Console.WriteLine($"Invoice batch complete. ✅ {success}, ❌ {errors}");
            },
            options: new RunTemporaryOptions
            {
                Timeout = TimeSpan.FromSeconds(5),
                PrefetchCount = 5,
                QueuePrefixName = "invoice",
                CorrelationId = Guid.NewGuid().ToString()
            }
        );

        Console.WriteLine("Batch dispatch complete — processing will continue in background.");
    }
}

```

