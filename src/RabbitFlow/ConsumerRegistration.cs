using EasyRabbitFlow.Exceptions;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EasyRabbitFlow
{
    /// <summary>
    /// Extension methods for registering RabbitMQ consumers.
    /// </summary>
    public static class ConsumerRegistration
    {
        /// <summary>
        /// Initializes and registers a RabbitMQ consumer for the specified event type and consumer.
        /// </summary>
        /// <typeparam name="TEventType">The type of event the consumer will handle.</typeparam>
        /// <typeparam name="TConsumer">The type of the consumer handling the event.</typeparam>
        /// <param name="rootServiceProvider">The service provider used to resolve dependencies.</param>
        /// <param name="settings">Optional settings for consumer registration.</param>
        /// <param name="cancellationToken"></param>
        /// <returns>The service provider.</returns>
        /// <exception cref="Exception">Thrown if required services are not found in the service provider or if consumer interface contracts are violated.</exception>
        public static async Task<IServiceProvider> InitializeConsumerAsync<TEventType, TConsumer>(this IServiceProvider rootServiceProvider, Action<ConsumerRegisterSettings>? settings = null, CancellationToken cancellationToken = default) where TConsumer : class where TEventType : class
        {
            var consumer_settings = new ConsumerRegisterSettings();

            settings?.Invoke(consumer_settings);

            if (!consumer_settings.Active)
            {
                return rootServiceProvider;
            }

            var logger = rootServiceProvider.GetRequiredService<ILogger<TConsumer>>();

            var factory = rootServiceProvider.GetRequiredService<ConnectionFactory>() ?? throw new Exception("ConnectionFactory was not found in Service Collection");

            var jsonSerializerOption = rootServiceProvider.GetKeyedService<JsonSerializerOptions>("RabbitFlowJsonSerializer") ?? JsonSerializerOptions.Web;

            var consumerOptions = rootServiceProvider.GetRequiredService<ConsumerSettings<TConsumer>>() ?? throw new Exception("ConsummerOptions was not found in Service Collection");

            var retryPolicy = rootServiceProvider.GetService<RetryPolicy<TConsumer>>() ?? new RetryPolicy<TConsumer> { MaxRetryCount = 1 };

            var customDeadletter = rootServiceProvider.GetService<CustomDeadLetterSettings<TConsumer>>() ?? null;

            var consumerType = typeof(TConsumer);

            string deadLetterQueueName = string.Empty;

            var consumerAbstraction = consumerType.GetInterfaces().FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IRabbitFlowConsumer<>)) ??
                throw new RabbitFlowException($"[RABBIT-FLOW]: {consumerType.Name} must implement IRabbitFlowConsumer<T> to be registered as a consumer.");

            var eventType = consumerAbstraction.GetGenericArguments().First();

            if (eventType != typeof(TEventType))
            {
                throw new RabbitFlowException($"[RABBIT-FLOW]: {consumerType.Name} must implement IRabbitFlowConsumer<{typeof(TEventType).Name}>.");
            }

            var connectionId = consumerOptions.ConsumerId ?? consumerOptions.QueueName;

            var connection = await factory.CreateConnectionAsync($"Consumer_{connectionId}", cancellationToken);

            var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

            channel.ContinuationTimeout = consumerOptions.Timeout;

            await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: consumerOptions.PrefetchCount, global: false, cancellationToken);

            if (consumerOptions.AutoGenerate)
            {
                var autoGenerateSettings = rootServiceProvider.GetService<AutoGenerateSettings<TConsumer>>() ?? new AutoGenerateSettings<TConsumer>();

                var exchangeName = autoGenerateSettings.ExchangeName ?? $"{consumerOptions.QueueName}-exchange";

                var queueName = consumerOptions.QueueName;

                var args = autoGenerateSettings.Args;

                var routingKey = autoGenerateSettings.RoutingKey ?? $"{consumerOptions.QueueName}-routing-key";

                if (autoGenerateSettings.GenerateDeadletterQueue)
                {
                    deadLetterQueueName = $"{queueName}-deadletter";

                    var deadLetterExchange = $"{queueName}-deadletter-exchange";

                    var deadLetterRoutingKey = $"{queueName}-deadletter-routing-key";

                    await channel.QueueDeclareAsync(queue: deadLetterQueueName, durable: autoGenerateSettings.DurableQueue, exclusive: false, autoDelete: autoGenerateSettings.AutoDeleteQueue, arguments: null, cancellationToken: cancellationToken);

                    await channel.ExchangeDeclareAsync(exchange: deadLetterExchange, type: "direct", durable: autoGenerateSettings.DurableExchange, cancellationToken: cancellationToken);

                    await channel.QueueBindAsync(queue: deadLetterQueueName, exchange: deadLetterExchange, routingKey: deadLetterRoutingKey);

                    args ??= new Dictionary<string, object?>();

                    args["x-dead-letter-exchange"] = deadLetterExchange;

                    args["x-dead-letter-routing-key"] = deadLetterRoutingKey;
                }

                // Declare queue
                await channel.QueueDeclareAsync(queue: queueName, durable: autoGenerateSettings.DurableQueue, exclusive: autoGenerateSettings.ExclusiveQueue, autoDelete: autoGenerateSettings.AutoDeleteQueue, arguments: args);

                if (autoGenerateSettings.GenerateExchange)
                {
                    // Declare exchange
                    await channel.ExchangeDeclareAsync(exchange: exchangeName, type: autoGenerateSettings.ExchangeType.ToString().ToLower(), durable: autoGenerateSettings.DurableExchange);

                    // Bind queue to exchange
                    await channel.QueueBindAsync(queue: queueName, exchange: exchangeName, routingKey: routingKey);
                }
            }

            var rabbitConsumer = new AsyncEventingBasicConsumer(channel);

            var semaphore = new SemaphoreSlim(consumerOptions.PrefetchCount);

            rabbitConsumer.ReceivedAsync += async (sender, args) =>
            {
                await semaphore.WaitAsync(cancellationToken);

                var body = args.Body.ToArray();

                var message = Encoding.UTF8.GetString(body);

                TEventType? @event = (TEventType?)JsonSerializer.Deserialize(message, typeof(TEventType), jsonSerializerOption)
                 ?? throw new RabbitFlowException("[RABBIT-FLOW]: Message deserialization failed. Ensure the message format is valid and matches the expected type.");

                IServiceScope? scope = null;

                object? consumerService = null;

                Exception? lastException = null;

                int retryCount = retryPolicy.MaxRetryCount;
                
                var processingTask = Task.Run(async () =>
                {
                    try
                    {
                        scope = rootServiceProvider.CreateScope();

                        try
                        {
                            if (consumer_settings.CreateNewInstancePerMessage)
                            {
                                var scopedServiceProvider = scope.ServiceProvider;

                                consumerService = scopedServiceProvider.GetRequiredService(consumerAbstraction) ?? throw new Exception("[RABBIT-FLOW]: Consumer was not found in Service Collection");
                            }
                            else
                            {
                                consumerService = rootServiceProvider.GetRequiredService(consumerAbstraction) ?? throw new Exception("[RABBIT-FLOW]: Consumer was not found in Service Collection");
                            }
                        }
                        catch (InvalidOperationException ex)
                        {
                            throw new RabbitFlowException("[RABBIT-FLOW]: Failed to resolve consumer service. Ensure all dependencies are correctly registered and configured. If CreateNewInstancePerMessage is set to false, all services must be Singleton.", ex);
                        }

                        while (retryCount > 0)
                        {
                            using var attemptCts = new CancellationTokenSource(channel.ContinuationTimeout);

                            var attemptCt = attemptCts.Token;
                            
                            try
                            {
                                if (!(consumerService is IRabbitFlowConsumer<TEventType>))
                                {
                                    throw new RabbitFlowException($"[RABBIT-FLOW]: Consumer service does not implement IRabbitFlowConsumer<{typeof(TEventType).Name}>. This is likely due to a misconfiguration.");
                                }

           
                                await (consumerService as IRabbitFlowConsumer<TEventType>)!.HandleAsync(@event, attemptCt).ConfigureAwait(false);
                                
                                await channel.BasicAckAsync(args.DeliveryTag, false);
                                
                                break;
                            }
                            catch (OperationCanceledException ex) when (ex.CancellationToken == attemptCt)
                            {
                                lastException = ex;
                                
                                await HandleExceptionRetry(ex, retryPolicy, retryCount, channel, logger);
                                
                                retryCount--;
                            }
                            catch (TaskCanceledException ex) when (ex.CancellationToken == attemptCt)
                            {
                                lastException = ex;
                                
                                await HandleExceptionRetry(ex, retryPolicy, retryCount, channel, logger);
                                
                                retryCount--;
                            }
                            catch (TaskCanceledException ex)
                            {
                                await HandleErrorAsync(consumerOptions.AutoAckOnError, channel, args.DeliveryTag, consumerOptions.ExtendDeadletterMessage, deadLetterQueueName, (TEventType?)@event, ex, jsonSerializerOption, cancellationToken: cancellationToken);

                                logger.LogError(ex, "[RABBIT-FLOW]: Task was unexpectedly canceled while processing the message. This may indicate a timeout or other issue.");

                                break;
                            }
                            catch (OperationCanceledException ex)
                            {
                                await HandleErrorAsync(consumerOptions.AutoAckOnError, channel, args.DeliveryTag, consumerOptions.ExtendDeadletterMessage, deadLetterQueueName, (TEventType?)@event, ex, jsonSerializerOption, cancellationToken: cancellationToken);

                                logger.LogError(ex, "[RABBIT-FLOW]: Operation was unexpectedly canceled while processing the message. Investigate the root cause to determine if it's due to a timeout or cancellation token.");

                                break;
                            }
                            catch (RabbitFlowTransientException ex)
                            {
                                await HandleExceptionRetry(ex, retryPolicy, retryCount, channel, logger);

                                retryCount--;
                            }
                            catch (Exception ex)
                            {
                                await HandleErrorAsync(consumerOptions.AutoAckOnError, channel, args.DeliveryTag, consumerOptions.ExtendDeadletterMessage, deadLetterQueueName, (TEventType?)@event, ex, jsonSerializerOption, cancellationToken: cancellationToken);

                                logger.LogError(ex, "[RABBIT-FLOW]: An unexpected error occurred while processing the message. No further retries will be attempted. To enable automatic retry for specific errors, catch them in your handler and throw a RabbitFlowTransientException instead.");

                                break;
                            }
                            finally
                            {
                                scope?.Dispose();   
                            }
                        }

                        if (retryCount == 0)
                        {
                            logger.LogError("[RABBIT-FLOW]: Maximum retry attempts reached ({maxRetryCount}) while processing the message. Message will be nacked or acknowledged depending on configuration.", retryPolicy.MaxRetryCount);

                            var exception = new RabbitFlowOverRetriesException(retryPolicy.MaxRetryCount, lastException);
                           
                            await HandleErrorAsync(
                                consumerOptions.AutoAckOnError, 
                                channel, args.DeliveryTag, 
                                consumerOptions.ExtendDeadletterMessage, 
                                deadLetterQueueName, 
                                (TEventType?)@event,
                                exception: exception, 
                                serializerOptions: jsonSerializerOption, 
                                cancellationToken: cancellationToken);

                            if (customDeadletter != null)
                            {
                                var publisher = rootServiceProvider.GetRequiredService<IRabbitFlowPublisher>();

                                if (@event != null)
                                {
                                    await publisher.PublishAsync((TEventType)@event, customDeadletter.DeadletterQueueName, publisherId: "custom-dead-letter");
                                }

                            }
                        }
                    }
                    finally
                    {
                        scope?.Dispose();
                        semaphore.Release();
                    }
                }, cancellationToken)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                    {
                        logger.LogError(t.Exception.Flatten(),
                            "[RABBIT-FLOW]: Unhandled exception in message processing task");

                    }
                    else if (t.IsCanceled)
                    {
                        logger.LogWarning("[RABBIT-FLOW]: Message processing task was canceled");
                    }

                }, TaskScheduler.Default);
            };

            await channel.BasicConsumeAsync(queue: consumerOptions.QueueName, autoAck: false, consumer: rabbitConsumer, cancellationToken);

            return rootServiceProvider;
        }


        private static async Task HandleExceptionRetry<TConsumer>(
            Exception ex, 
            RetryPolicy<TConsumer> retryPolicy, 
            int retryCount, 
            IChannel channel, 
            ILogger<TConsumer> logger) where TConsumer : class
        {
            var currentAttempt = retryPolicy.MaxRetryCount - retryCount + 1;

            var delay = retryPolicy.RetryInterval;

            if (retryPolicy.ExponentialBackoff && currentAttempt > 1)
            {
                delay = retryPolicy.RetryInterval * (int)Math.Pow(retryPolicy.ExponentialBackoffFactor, currentAttempt - 1);
            }

            logger.LogWarning("[RABBIT-FLOW]: Error processing the message. Attempt: {currentAttempt}/{maxRetryCount}. Delay: {delay} ms. Exception Message: {message}",
                currentAttempt, retryPolicy.MaxRetryCount, delay, ex.Message);

            await Task.Delay(delay, CancellationToken.None);    
        }

        private static async Task HandleErrorAsync<TEventType>(
            bool autoAck,
            IChannel channel,
            ulong deliveryTag,
            bool extendDeadletterMessage = false,
            string deadletterQueue = "",
            TEventType? @event = null,
            Exception? exception = null,
            JsonSerializerOptions? serializerOptions = null,
            CancellationToken cancellationToken = default) where TEventType : class
        {
            if (autoAck)
            {
                await channel.BasicAckAsync(deliveryTag, false, cancellationToken);
            }
            else
            {
                if (extendDeadletterMessage && !string.IsNullOrEmpty(deadletterQueue))
                {
                    var innerExceptions = new List<Exception>();

                    var tempException = exception;

                    const int MaxInnerExceptionDepth = 10;

                    while (tempException?.InnerException != null && innerExceptions.Count < MaxInnerExceptionDepth)
                    {
                        innerExceptions.Add(tempException.InnerException);

                        tempException = tempException.InnerException;
                    }

                    var extendedMessage = new
                    {
                        dateUtc = DateTime.UtcNow,
                        messageType = typeof(TEventType).Name,
                        messageData = @event,
                        exceptionType = exception?.GetType().Name,
                        errorMessage = exception?.Message,
                        stackTrace = exception?.StackTrace,
                        source = exception?.Source,
                        innerExceptions = innerExceptions.Select(e => new
                        {
                            exceptionType = e.GetType().Name,
                            errorMessage = e.Message,
                            stackTrace = e.StackTrace,
                            source = e.Source
                        }).ToList()
                    };

                    await channel.BasicPublishAsync(exchange: "", routingKey: deadletterQueue, body: Encoding.UTF8.GetBytes(JsonSerializer.Serialize(extendedMessage, options: serializerOptions)), cancellationToken: cancellationToken);

                    await channel.BasicAckAsync(deliveryTag, false, cancellationToken);
                }
                else
                {
                    await channel.BasicNackAsync(deliveryTag, false, false, cancellationToken);
                }
            }
        }
    }
}