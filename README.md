## RabbitFlow Documentation

Welcome to the documentation for the **RabbitFlow** library! This guide will walk you through the process of configuring and using RabbitMQ consumers in your application using RabbitFlow.

### Table of Contents

1. [Introduction](#introduction)
2. [Installation](#installation)
3. [Configuration](#configuration)
   - [Host Configuration](#host-configuration)
   - [JSON Serialization Options](#json-serialization-options)
   - [Publisher Options](#publisher-options)
4. [Consumers](#consumers)
   - [Creating Consumers](#creating-consumers)
   - [Consumer Settings](#consumer-settings)
   - [Retry Policies](#retry-policies)
   - [Consumer Interface](#consumer-interface)
5. [Middleware](#middleware)
6. [Publishing Messages](#publishing-messages)
7. [Queue State](#queue-state)
8. [Configure Temporary Queues](#configure-temporary-queues)
9. Configure Deadletter Queues.
10. [Examples](#examples)
   - [Basic Consumer Setup](#basic-consumer-setup)
   - [Middleware Usage](#middleware-usage)

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

Furthermore, the 'PublishAsync' method of the publisher offers an optional parameter named 'useNewConnection,' which defaults to 'false.' This parameter allows for the explicit control of whether a new connection should be established, irrespective of the global configuration set for the publisher. It is essential to note that the same publisher can be employed to transmit messages to different queues, each with its unique configuration settings.
```csharp
 opt.ConfigurePublisher(publisherSettings => publisherSettings.DisposePublisherConnection = true);
```

### 4. Consumers
- #### 4.1 Creating Consumers
Define and configure consumers for specific queues using the AddConsumer method:
```csharp
opt.AddConsumer("email-queue", consumerSettings =>
{
    consumerSettings.PrefetchCount = 1;
    // ... other settings ...
    consumerSettings.SetConsumerHandler<EmailConsumer>();
});
```
#### Consumer Settings
    - AutoAckOnError: Gets or sets a value indicating whether messages are automatically acknowledged in case of an error. 
      Defaults to True (Reject and Dispose) .
    - AutoGenerate: Set the intention of automatically create the queue + exchange and bind them.
    - PrefetchCount: Limit unacknowledged messages to consume at a time.
    - Timeout: Set maximum processing time for each message.

#### 4.2 Retry Policies
Configure a retry policy for handling message processing failures:

```
consumerSettings.ConfigureRetryPolicy<EmailConsumer>(retryPolicy =>
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
```


### 5. Middleware
Use the UseConsumer middleware to efficiently process messages using registered consumers:
```
app.UseConsumer<EmailEvent, EmailConsumer>();

app.UseConsumer<WhatsAppEvent, WhatsAppConsumer>(opt =>
{
    opt.PerMessageInstance = true;
    opt.Active = false;
});
```
#### Middleware Options
Use the ConsumerRegisterSettings class to configure the middleware:

- Active: If set to true, the middleware will be enabled. If set to false, the middleware will be disabled, meaning the consumer won't process any message from the queue.
- PerMessageInstance: If set to true, a new instance of the consumer service handler will be created for each message. If set to false, the same instance of the service will be used for all messages.

### 6. Publishing Messages
Publisher Interface

Use the IRabbitFlowPublisher interface to publish messages to a RabbitMQ exchange:
```csharp
public interface IRabbitFlowPublisher
{
    Task PublishAsync<TEvent>(TEvent message, string exchangeName, string routingKey, bool useNewConnection = false);

    Task PublishAsync<TEvent>(TEvent @event, string queueName, bool useNewConnection = false);
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
}

```

### 8. Configure Temporary Queues
RabbitFlow's temporary queues functionality offers a convenient way to manage short-lived queues in RabbitMQ. Temporary queues are ideal for scenarios where you need to exchange messages efficiently between components without the need for long-term queue management. With RabbitFlow, you can easily publish events to these queues and establish temporary consumer handlers for efficient message consumption. This feature simplifies the process of handling transient message exchanges, allowing you to focus on your application's core functionality.

To use temporary queues, you must first configure the RabbitFlow host as mentioned before.
Then you can use the interface `IRabbitFlowTemporaryQueue`.

The `IRabbitFlowTemporaryQueue` interface represents a service for managing temporary RabbitMQ queues and handling message publishing and consumption. This interface defines methods that allow you to publish events to queues and set up temporary consumer handlers.

### Methods
```
1. PublishAsync<TEvent>(string queueName, TEvent @event, CancellationToken cancellationToken)
```
Publishes an event to a specified queue asynchronously.

- `queueName`: The name of the queue to publish the event to.
- `event`: The event to publish.
- `cancellationToken`: A cancellation token to cancel the operation.
- 

```
2. void SetTemporaryConsumerHandler<TEvent>(string queueName, TemporaryConsummerSettings settings, Func<TEvent, CancellationToken, Task> temporaryConsumerHandler = default!);
```

Sets up a temporary consumer handler for the specified queue, allowing message consumption.

- `queueName`: The name of the queue to consume from.
- `settings`: Temporary consumer settings.
- `temporaryConsumerHandler`: The handler to process received events.

`TemporaryConsummerSettings` parameter contains the following properties:
- PrefetchCount: Gets or sets the number of messages that the consumer can prefetch.
- Timeout: Gets or sets the timeout duration for processing a single message.

`temporaryConsumerHandler` Is a function that takes the following parameters:
- `event`: The event to process.
- `cancellationToken`: A cancellation token to cancel the operation.


Check the example below for more details.

#### 8.1 Temporary Queues Lifecycle
Temporary queues are created when you define a temporary consumer handler, and they are deleted when the service is manually disposed or 
automatically when it detects that in a max period of 5 minutes, no message was sent to the queue.

This means that you can handle the disposal of the service manually or let it dispose by itself.

### 9. Deadletter Queues
Configuring dead-letter queues offers various options. If you opt for the approach of creating queues and exchanges using infrastructure first and then binding a specific queue to act as the dead-letter queue for the main queue, the process will function as expected, and failed messages will end up in that dead-letter queue. Additionally, you can manually configure failed messages to be sent to another specific queue using the ConfigureCustomDeadletter method when configuring the Consumer.

Alternatively, if you choose the approach where the application utilizing the library is responsible for creating the queues, exchanges, and binding them, you can utilize the ConfigureAutoGenerate method to select this behavior. In both approaches, ConfigureCustomDeadletter can be configured and will function independently.

### 10. Examples
#### 10.1 Basic Consumer Setup
```csharp
opt.AddConsumer("whatsapp-queue", consumerSettings =>
{
    consumerSettings.SetConsumerHandler<WhatsAppConsumer>();
});
```
Middleware Usage
```csharp
app.UseConsumer<EmailEvent, EmailConsumer>();
```

#### 10.2 Consumer Implementation
```csharp
public class EmailConsumer : IRabbitFlowConsumer<EmailEvent>
{
	public async Task HandleAsync(EmailEvent message, CancellationToken cancellationToken)
	{
		// Process message
	}
}
```

#### 10.3 Set Temporary Queues
```
public class TemporaryQueueConsumer
{
   
    private readonly IRabbitFlowTemporaryQueue _rabbitFlowTemporary;
    private readonly ISomeTranscientService _someTranscientService;
    private readonly ILogger<TemporaryQueueConsumer> _logger;
    private int totalMessages = 0;
    private int processedMessages = 0;

    public TemporaryQueueConsumer(IRabbitFlowTemporaryQueue rabbitFlowTemporary, ISomeTranscientService someTranscientService, ILogger<TemporaryQueueConsumer> logger)
    {
        _rabbitFlowTemporary = rabbitFlowTemporary;
        _someTranscientService = someTranscientService;

        //setting up a temporary consumer handler for the queue "long-task-queue"
        _rabbitFlowTemporary.SetTemporaryConsumerHandler<LongTaskItem>("long-task-queue", new TemporaryConsummerSettings { PrefetchCount = 1 }, ConsumerHandler);
        
        _logger = logger;
    }
}
```
In the TemporaryQueueConsumer class constructor, an instance of IRabbitFlowTemporaryQueue is injected, allowing you to interact with temporary queues. 

The SetTemporaryConsumerHandler method is used to establish a temporary consumer handler for the "long-task-queue". 

The consumer handler method ConsumerHandler is defined later in the example.

##### Consumer Handler

```
private async Task ConsumerHandler(LongTaskItem longTaskItem, CancellationToken cancellation)
{
    LongTimeOperation(longTaskItem);

    processedMessages++;

    if (processedMessages == totalMessages)
    {
        _rabbitFlowTemporary.Dispose();
    }

    await Task.CompletedTask;
}
```
The ConsumerHandler method processes received messages from the "long-task-queue". In this example, a long operation LongTimeOperation is simulated. 
Once all messages are processed, the temporary consumer is manually disposed.
