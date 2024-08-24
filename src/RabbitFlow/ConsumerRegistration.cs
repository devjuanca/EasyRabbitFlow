using Microsoft.Extensions.DependencyInjection;
using RabbitFlow.Services;
using RabbitFlow.Settings;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RabbitFlow.Configuration
{
    /// <summary>
    /// Extension methods for registering RabbitMQ consumers.
    /// </summary>
    public static class ConsumerRegistration
    {
        /// <summary>
        /// Initializes and starts a RabbitMQ consumer for a specific event type.
        /// Configures the consumer's settings, establishes a connection to RabbitMQ, and starts listening to the specified queue.
        /// </summary>
        /// <typeparam name="TEventType">The type of event the consumer will handle.</typeparam>
        /// <param name="configurator">The RabbitFlowConfigurator instance used to configure RabbitMQ services.</param>
        /// <param name="settings">Optional action to configure additional consumer settings.</param>
        public static RabbitFlowConfigurator InitializeConsumer<TEventType>(this RabbitFlowConfigurator configurator, Action<ConsumerRegisterSettings>? settings = null) where TEventType : class
        {
            var consumer_settings = new ConsumerRegisterSettings();

            var services = configurator.Services;

            settings?.Invoke(consumer_settings);

            if (!consumer_settings.Active)
            {
                return configurator;
            }

            var consumerType = configurator.ConsumerType;

            var serviceProvider = services.BuildServiceProvider();

            var factory = serviceProvider.GetRequiredService<ConnectionFactory>() ?? throw new Exception("ConnectionFactory was not found in Service Collection");

            var jsonSerializerOption = serviceProvider.GetService<JsonSerializerOptions>() ?? new JsonSerializerOptions();

            var consumerOptionsType = typeof(ConsumerSettings<>).MakeGenericType(consumerType);

            var consumerOptions = (dynamic)serviceProvider.GetRequiredService(consumerOptionsType) ?? throw new Exception("ConsumerOptions was not found in Service Collection");

            var queueName = (string?)consumerOptions.QueueName;

            var retryPolicyType = typeof(RetryPolicy<>).MakeGenericType(consumerType);

            var retryPolicy = (dynamic?)serviceProvider.GetService(retryPolicyType);

            var customDeadletterType = typeof(CustomDeadLetterSettings<>).MakeGenericType(consumerType);

            var customDeadletter = (dynamic?)serviceProvider.GetService(customDeadletterType);

            var customDeadletterQueueName = (string?)customDeadletter?.DeadletterQueueName;

            var consumerAbstraction = consumerType?.GetInterfaces().FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IRabbitFlowConsumer<>)) ?? throw new Exception("[RABBIT-FLOW]: Consumer must implement IRabbitFlowConsumer<T>");

            var eventType = consumerAbstraction.GetGenericArguments().First();

            if (eventType != typeof(TEventType)) throw new Exception($"[RABBIT-FLOW]: Consumer must implement IRabbitFlowConsumer<{typeof(TEventType).Name}>");

            var connectionId = consumerOptions.ConsumerId ?? consumerOptions.QueueName;

            var connection = factory.CreateConnection($"Consumer_{connectionId}");

            var channel = connection.CreateModel();

            channel.ContinuationTimeout = consumerOptions.Timeout;

            channel.BasicQos(prefetchSize: 0, prefetchCount: consumerOptions.PrefetchCount, global: false);

            if (consumerOptions.AutoGenerate)
            {
                var autoGenerateSettingsType = typeof(AutoGenerateSettings<>).MakeGenericType(consumerType);

                var autoGenerateSettings = (dynamic?)serviceProvider.GetService(autoGenerateSettingsType);

                var exchangeName = (string?)autoGenerateSettings?.ExchangeName ?? $"{consumerOptions.QueueName}-exchange";

                var generateExchange = (bool?)autoGenerateSettings?.GenerateExchange ?? false;

                var durableExchange = (bool?)autoGenerateSettings?.DurableExchange ?? false;

                var durableQueue = (bool?)autoGenerateSettings?.DurableQueue ?? false;

                var autoDeleteQueue = (bool?)autoGenerateSettings?.AutoDeleteQueue ?? false;

                var exclusiveQueue = (bool?)autoGenerateSettings?.ExclusiveQueue ?? false;

                var exchangeType = (autoGenerateSettings?.ExchangeType as Settings.ExchangeType?).ToString().ToLower();

                var args = (IDictionary<string, object>?)autoGenerateSettings?.Args;

                var routingKey = (string?)autoGenerateSettings?.RoutingKey ?? $"{consumerOptions.QueueName}-routing-key";

                if ((bool?)autoGenerateSettings?.GenerateDeadletterQueue ?? false)
                {
                    var deadLetterQueueName = $"{queueName}-deadletter";

                    var deadLetterExchange = $"{queueName}-deadletter-exchange";

                    var deadLetterRoutingKey = $"{queueName}-deadletter-routing-key";


                    channel.QueueDeclare(queue: deadLetterQueueName, durable: durableQueue, exclusive: false, autoDelete: autoDeleteQueue, arguments: null);

                    channel.ExchangeDeclare(exchange: deadLetterExchange, type: "direct", durable: durableExchange);

                    channel.QueueBind(queue: deadLetterQueueName, exchange: deadLetterExchange, routingKey: deadLetterRoutingKey);

                    if (args is null)
                    {
                        args = new Dictionary<string, object>
                        {
                            {"x-dead-letter-exchange", deadLetterExchange},
                            {"x-dead-letter-routing-key", deadLetterRoutingKey}
                        };
                    }
                    else
                    {
                        args.Add("x-dead-letter-exchange", deadLetterExchange);
                        args.Add("x-dead-letter-routing-key", deadLetterRoutingKey);
                    }
                }

                // Declare queue
                channel.QueueDeclare(queue: queueName, durable: durableQueue, exclusive: exclusiveQueue, autoDelete: autoDeleteQueue, arguments: args);

                if (generateExchange)
                {
                    // Declare exchange
                    channel.ExchangeDeclare(exchange: exchangeName, type: exchangeType, durable: durableExchange);

                    // Bind queue to exchange
                    channel.QueueBind(queue: queueName, exchange: exchangeName, routingKey: routingKey);
                }
            }

            var rabbitConsumer = new EventingBasicConsumer(channel);

            object? consumerService = null;

            rabbitConsumer.Received += async (sender, args) =>
            {
                using var cancelationSource = new CancellationTokenSource(channel.ContinuationTimeout);

                var cancellationToken = cancelationSource.Token;

                var scope = serviceProvider.CreateScope();

                var scopedServiceProvider = scope.ServiceProvider;

                consumerService = scopedServiceProvider.GetRequiredService(consumerAbstraction) ?? throw new Exception("Consumer was not found in Service Collection");

                var retryCount = retryPolicy?.MaxRetryCount ?? 1;

                bool shouldDelay = retryCount > 1;

                string message = string.Empty;

                object? @event = null;

                while (retryCount > 0)
                {
                    try
                    {
                        var body = args.Body.ToArray();

                        message = Encoding.UTF8.GetString(body);

                        @event = JsonSerializer.Deserialize(message, typeof(TEventType), jsonSerializerOption);

                        if (@event == null)
                        {
                            throw new Exception("[RABBIT-FLOW]: Failed to serialize the message. Check the message format or serialization settings.");
                        }

                        if (!(consumerService is IRabbitFlowConsumer<TEventType>))
                        {
                            throw new Exception("[RABBIT-FLOW]: Consumer service must implement IRabbitFlowConsumer<TEventType>. Ensure that all consumer services adhere to this interface to maintain the flow. This exception should never be thrown under normal circumstances.");
                        }

                        await (consumerService as IRabbitFlowConsumer<TEventType>)!.HandleAsync((TEventType)@event, cancellationToken).ConfigureAwait(false);

                        channel.BasicAck(args.DeliveryTag, false);

                        break;
                    }
                    catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken)
                    {
                        cancellationToken = await HandleExceptionRetry(ex, retryPolicy, retryCount, channel);

                        retryCount--;
                    }
                    catch (TaskCanceledException ex) when (ex.CancellationToken == cancellationToken)
                    {
                        cancellationToken = await HandleExceptionRetry(ex, retryPolicy, retryCount, channel);

                        retryCount--;
                    }
                    catch (TaskCanceledException)
                    {
                        Console.WriteLine("[RABBIT-FLOW]: Task canceled for an unexpected reason while receiving message.");
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine("[RABBIT-FLOW]: Task canceled for an unexpected reason while receiving message.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (shouldDelay)
                        {
                            cancellationToken = await HandleExceptionRetry(ex, retryPolicy, retryCount, channel);
                        }
                        retryCount--;
                    }
                    finally
                    {
                        scope?.Dispose();
                    }
                }

                if (retryCount == 0)
                {
                    Console.WriteLine($"[RABBIT-FLOW]: Error receiving message. Retry Count: {retryCount}.");

                    if (consumerOptions.AutoAckOnError)
                    {
                        channel.BasicAck(args.DeliveryTag, false);

                    }
                    else
                    {
                        channel.BasicNack(args.DeliveryTag, false, false);
                    }

                    if (customDeadletter != null && !string.IsNullOrEmpty(customDeadletterQueueName))
                    {
                        var publisher = serviceProvider.GetRequiredService<IRabbitFlowPublisher>();

                        if (@event != null)
                        {
                            await publisher.PublishAsync((TEventType)@event, customDeadletterQueueName!, publisherId: "custom-dead-letter");
                        }
                    }
                }
            };

            channel.BasicConsume(queue: queueName, autoAck: false, consumer: rabbitConsumer);

            return configurator;
        }

        public static async Task<CancellationToken> HandleExceptionRetry<TConsumer>(Exception ex, RetryPolicy<TConsumer> retryPolicy, int retryCount, IModel channel) where TConsumer : class
        {
            var maxRetryCount = retryPolicy.MaxRetryCount;

            var remainingRetries = maxRetryCount - retryCount;

            var delay = retryPolicy.RetryInterval;

            if (retryPolicy.ExponentialBackoff && remainingRetries > 0)
            {
                delay = retryPolicy.RetryInterval * (int)Math.Pow(retryPolicy.ExponentialBackoffFactor, remainingRetries);
            }

            Console.WriteLine($"[RABBIT-FLOW]: Error processing the message. Retry Count: {retryCount}. Delay: {delay} ms. Exception Message: {ex.Message}");

            await Task.Delay(delay, CancellationToken.None);

            using var cts = new CancellationTokenSource(channel.ContinuationTimeout);

            return cts.Token;
        }
    }
}