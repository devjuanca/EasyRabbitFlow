## RabbitFlow Documentation

Welcome to the documentation for the **RabbitFlow** library! This guide will walk you through the process of configuring and using RabbitMQ consumers in your application using RabbitFlow.

### Table of Contents

1. [Introduction](#introduction)
2. [Install](#install)
3. [Configuration](#configuration)
   - [Host Configuration](#host-configuration)
   - [JSON Serialization Options](#json-serialization-options)
   - [Publisher Options](#publisher-options)
4. [Consumers](#consumers)
   - [Adding Consumers](#adding-consumers)
   - [Retry Policies](#retry-policies)
   - [Consumer Interface Implementation](#consumer-interface-implementation)
5. [Initialize Consumers](#initialize-consumers)
6. [Publishing Messages](#publishing-messages)
7. [Queue State](#queue-state)
8. [Temporary Queue Processing](#Temporary Message Processing with `IRabbitFlowTemporary`)


### 1. Introduction

RabbitFlow is an intuitive library designed to simplify the management of RabbitMQ consumers within your application. This documentation provides step-by-step instructions for setting up, configuring, and using RabbitFlow effectively in your projects.

RabbitFlow is specifically designed to handle two approaches: pre-defined exchanges and queues within RabbitMQ, as well as the dynamic creation of new ones as needed. The rationale behind this approach is to offer flexibility in managing RabbitMQ infrastructure while maintaining simplicity in usage.

Now, let's dive into the details of setting up, configuring, and using RabbitFlow in your projects.
### 2. Install

To install the **RabbitFlow** library into your project, you can use the NuGet package manager:

```bash
dotnet add package EasyRabbitFlow
```

### 3. Configuration
To configure RabbitFlow in your application, use the AddRabbitFlow method:
```csharp
builder.Services.AddRabbitFlow(opt =>
{
    // Configure host settings
    opt.ConfigureHost(hostSettings =>
    {
        hostSettings.Host = "rabbitmq.example.com";
        hostSettings.Username = "guest";
        hostSettings.Password = "guest";
    });

    // Configure JSON serialization options. [OPTIONAL]
    opt.ConfigureJsonSerializerOptions(jsonSettings =>
    {
        jsonSettings.PropertyNameCaseInsensitive = true;
        jsonSettings.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        jsonSettings.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

    // Configure Publisher. [OPTIONAL]
    opt.ConfigurePublisher(publisherSettings => publisherSettings.DisposePublisherConnection = false);

    // Add and configure consumers
     settings.AddConsumer<TConsumer>(consumer=>{...});
});
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


### 5. Initialize Consumers
Use the `InitializeConsumer` extension method to efficiently process messages using registered consumers.

This method is an extension of `IServiceProvider`. It is intended for use when the application's ServiceProvider is already built, ensuring no second container is created to handle incoming messages.

This method resolves all required services and configurations, starting the message listener for the queue.

You can configure whether the consumer will be active and whether a new instance of the consumer should be created for each message. This ensures isolated processing in a scoped environment or a single instance for all incoming messages. Use this feature according to your needs.

```
var app = builder.Build();

app.Services.InitializeConsumer<EmailEvent, EmailConsumer>();

app.Services.InitializeConsumer<WhatsAppEvent, WhatsAppConsumer>(opt =>
{
    opt.PerMessageInstance = true;
    opt.Active = false;
});
```

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

### 8. Temporary Message Processing with `IRabbitFlowTemporary`

`IRabbitFlowTemporary` is a utility designed to simplify **fire-and-forget style workflows** where a batch of messages is sent to RabbitMQ, processed by handlers, and discarded — all within a **temporary queue**.

This is ideal for:
- Testing or debugging pipelines.
- One-time batch processing jobs.
- Ephemeral tasks that don’t require long-term persistence or consumer infrastructure.

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

