## RabbitFlow Documentation

Welcome to the documentation for the **RabbitFlow** library! This guide will walk you through the process of configuring and using RabbitMQ consumers in your application using RabbitFlow.

### Table of Contents

1. [Introduction](#introduction)
2. [Configuration](#installation)
3. [Configuration](#configuration)
   - [Host Configuration](#host-configuration)
   - [JSON Serialization Options](#json-serialization-options)
   - [Publisher Options](#publisher-options)
4. [Consumers](#consumers)
   - [Creating Consumers](#creating-consumers)
   - [Retry Policies](#retry-policies)
   - [Consumer Interface](#consumer-interface)
5. [Use Consumers](#use-consumers)
6. [Publishing Messages](#publishing-messages)
7. [Other Services](#other-services)
8. [Configure Temporary Queues](#configure-temporary-queues)


### 1. Introduction

RabbitFlow is an intuitive library designed to simplify the management of RabbitMQ consumers in your application. This documentation provides step-by-step instructions for setting up, configuring, and using RabbitFlow effectively in your projects.

RabbitFlow is specifically designed to handle two approaches, pre-defined exchanges and queues within RabbitMQ, as well as dynamically create new ones as needed. The rationale behind this approach is to provide flexibility in managing RabbitMQ infrastructure while also offering simplicity in usage.

Now, let's dive into the details of how to set up, configure, and use RabbitFlow effectively in your projects.
### 2. Installation

To integrate the **RabbitFlow** library into your project, you can use the NuGet package manager:

```
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

    // Configure JSON serialization options
    opt.ConfigureJsonSerializerOptions(jsonSettings =>
    {
        jsonSettings.PropertyNameCaseInsensitive = true;
        jsonSettings.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        jsonSettings.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

    // Add and configure consumers
    // ...
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
To configure RabbitFlow in your application, use the AddRabbitFlow method:
```csharp
opt.ConfigureJsonSerializerOptions(jsonSettings =>
{
    jsonSettings.PropertyNameCaseInsensitive = true;
    jsonSettings.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    jsonSettings.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
```
Configuring JSON Serialization Options is optional, if no configuration is provided, the default options will be used.

- #### 3.3 Publisher Options
Optionally, you may configure the publisher that you intend to use by defining the 'DisposePublisherConnection' variable. This variable determines whether the connection established by the publisher with RabbitMQ should be kept alive or terminated upon the completion of the process.
Default value is set to false.
```csharp
 opt.ConfigurePublisher(publisherSettings => publisherSettings.DisposePublisherConnection = true);
```

### 4. Consumers
- #### 4.1 Creating Consumers
Define and configure consumers for specific queues using the AddConsumer method:
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
Configure a retry policy for handling message processing failures:

```
consumerSettings.ConfigureRetryPolicy(retryPolicy =>
{
    retryPolicy.MaxRetryCount = 3;
    retryPolicy.RetryInterval = 1000;
    retryPolicy.ExponentialBackoff = true;
    retryPolicy.ExponentialBackoffFactor = 2;
});

```

#### 4.3 Consumer Interface

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


### 5. Use Consumers
Use the UseConsumer extension method to efficiently process messages using registered consumers:
```
app.UseConsumer<EmailEvent, EmailConsumer>();

app.UseConsumer<WhatsAppEvent, WhatsAppConsumer>(opt =>
{
    opt.PerMessageInstance = true;
    opt.Active = false;
});
```
####  Options
Use the ConsumerRegisterSettings class to configure the middleware:

- Active: If set to true, the middleware will be enabled. If set to false, the middleware will be disabled, meaning the consumer won't process any message from the queue.
- PerMessageInstance: If set to true, a new instance of the consumer service handler will be created for each message. If set to false, the same instance of the service will be used for all messages.
If set tu false every service injected in the consumer should be Singleton.
Default is set to false.

### 6. Publishing Messages
Publisher Interface

Use the IRabbitFlowPublisher interface to publish messages to a RabbitMQ exchange:
```csharp
public interface IRabbitFlowPublisher
{
  Task<bool> PublishAsync<TEvent>(TEvent message, string exchangeName, string routingKey, string publisherId = "", JsonSerializerOptions? jsonOptions = null) where TEvent : class;

  Task<bool> PublishAsync<TEvent>(TEvent message, string queueName, string publisherId = "", JsonSerializerOptions? jsonOptions = null) where TEvent : class;
}

```

### 7. Queue State
Queue State Interface

The IRabbitFlowState interface provides methods to query queue status:
```csharp
public interface IRabbitFlowState
{
    bool IsEmptyQueue(string queueName);
    uint GetQueueLength(string queueName);
    uint GetConsumersCount(string queueName);
    bool QueueHasConsumers(string queueName);
}

```
