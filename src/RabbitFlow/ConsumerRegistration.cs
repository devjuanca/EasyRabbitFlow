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
        /// <param name="serviceProvider">The service provider used to resolve dependencies.</param>
        /// <param name="settings">Optional settings for consumer registration.</param>
        /// <param name="cancellationToken"></param>
        /// <returns>The service provider.</returns>
        /// <exception cref="Exception">Thrown if required services are not found in the service provider or if consumer interface contracts are violated.</exception>
        public static async Task<IServiceProvider> InitializeConsumerAsync<TEventType, TConsumer>(this IServiceProvider serviceProvider, Action<ConsumerRegisterSettings>? settings = null, CancellationToken cancellationToken = default) where TConsumer : class where TEventType : class
        {
            var consumer_settings = new ConsumerRegisterSettings();

            settings?.Invoke(consumer_settings);

            if (!consumer_settings.Active)
            {
                return serviceProvider;
            }

            var logger = serviceProvider.GetRequiredService<ILogger<TConsumer>>();

            var factory = serviceProvider.GetRequiredService<ConnectionFactory>() ?? throw new Exception("ConnectionFactory was not found in Service Collection");

            var jsonSerializerOption = serviceProvider.GetService<JsonSerializerOptions>() ?? new JsonSerializerOptions();

            var consumerOptions = serviceProvider.GetRequiredService<ConsumerSettings<TConsumer>>() ?? throw new Exception("ConsummerOptions was not found in Service Collection");

            var retryPolicy = serviceProvider.GetService<RetryPolicy<TConsumer>>() ?? new RetryPolicy<TConsumer> { MaxRetryCount = 1 };

            var customDeadletter = serviceProvider.GetService<CustomDeadLetterSettings<TConsumer>>() ?? null;

            var consumerType = typeof(TConsumer);

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
                var autoGenerateSettings = serviceProvider.GetService<AutoGenerateSettings<TConsumer>>() ?? new AutoGenerateSettings<TConsumer>();

                var exchangeName = autoGenerateSettings.ExchangeName ?? $"{consumerOptions.QueueName}-exchange";

                var queueName = consumerOptions.QueueName;

                var args = autoGenerateSettings.Args;

                var routingKey = autoGenerateSettings.RoutingKey ?? $"{consumerOptions.QueueName}-routing-key";

                if (autoGenerateSettings.GenerateDeadletterQueue)
                {
                    var deadLetterQueueName = $"{queueName}-deadletter";

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

            object? consumerService = null;

            rabbitConsumer.ReceivedAsync += async (sender, args) =>
            {
                IServiceScope scope = default!;

                using var cancelationSource = new CancellationTokenSource(channel.ContinuationTimeout);

                var ct = cancelationSource.Token;

                try
                {
                    if (consumer_settings.CreateNewInstancePerMessage)
                    {
                        scope = serviceProvider.CreateScope();

                        var scopedServiceProvider = scope.ServiceProvider;

                        consumerService = scopedServiceProvider.GetRequiredService(consumerAbstraction) ?? throw new Exception("Consumer was not found in Service Collection");
                    }
                    else
                    {
                        consumerService = serviceProvider.GetRequiredService(consumerAbstraction) ?? throw new Exception("Consumer was not found in Service Collection");
                    }
                }
                catch (InvalidOperationException ex)
                {
                    throw new RabbitFlowException("[RABBIT-FLOW]: Failed to resolve consumer service. Ensure all dependencies are correctly registered and configured. If PerMessageInstance is set to false, all services must be Singleton.", ex);
                }

                var retryCount = retryPolicy.MaxRetryCount;

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
                            throw new RabbitFlowException("[RABBIT-FLOW]: Message deserialization failed. Ensure the message format is valid and matches the expected type.");
                        }

                        if (!(consumerService is IRabbitFlowConsumer<TEventType>))
                        {
                            throw new RabbitFlowException($"[RABBIT-FLOW]: Consumer service does not implement IRabbitFlowConsumer<{typeof(TEventType).Name}>. This is likely due to a misconfiguration.");
                        }

                        await (consumerService as IRabbitFlowConsumer<TEventType>)!.HandleAsync((TEventType)@event, ct).ConfigureAwait(false);

                        await channel.BasicAckAsync(args.DeliveryTag, false);

                        break;
                    }
                    catch (OperationCanceledException ex) when (ex.CancellationToken == ct)
                    {
                        ct = await HandleExceptionRetry(ex, retryPolicy, retryCount, channel, logger);

                        retryCount--;
                    }
                    catch (TaskCanceledException ex) when (ex.CancellationToken == ct)
                    {
                        ct = await HandleExceptionRetry(ex, retryPolicy, retryCount, channel, logger);

                        retryCount--;
                    }
                    catch (TaskCanceledException ex)
                    {
                        await HandleErrorAsync(consumerOptions.AutoAckOnError, channel, args.DeliveryTag, cancellationToken);

                        logger.LogError(ex, "[RABBIT-FLOW]: Task was unexpectedly canceled while processing the message. This may indicate a timeout or other issue.");

                        break;
                    }
                    catch (OperationCanceledException ex)
                    {
                        await HandleErrorAsync(consumerOptions.AutoAckOnError, channel, args.DeliveryTag, cancellationToken);

                        logger.LogError(ex, "[RABBIT-FLOW]: Operation was unexpectedly canceled while processing the message. Investigate the root cause to determine if it's due to a timeout or cancellation token.");

                        break;
                    }
                    catch (TranscientException ex)
                    {
                        if (shouldDelay)
                        {
                            ct = await HandleExceptionRetry(ex, retryPolicy, retryCount, channel, logger);
                        }
                        retryCount--;
                    }
                    catch (Exception ex)
                    {
                        await HandleErrorAsync(consumerOptions.AutoAckOnError, channel, args.DeliveryTag, cancellationToken);

                        logger.LogError(ex, "[RABBIT-FLOW]: An unexpected error occurred while processing the message. No further retries will be attempted. Ensure the handler implementation is resilient.");

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

                    await HandleErrorAsync(consumerOptions.AutoAckOnError, channel, args.DeliveryTag, cancellationToken);

                    if (customDeadletter != null)
                    {
                        var publisher = serviceProvider.GetRequiredService<IRabbitFlowPublisher>();

                        if (@event != null)
                        {
                            await publisher.PublishAsync((TEventType)@event, customDeadletter.DeadletterQueueName, publisherId: "custom-dead-letter");
                        }

                    }
                }
            };

            await channel.BasicConsumeAsync(queue: consumerOptions.QueueName, autoAck: false, consumer: rabbitConsumer, cancellationToken);

            return serviceProvider;
        }

        private static async Task<CancellationToken> HandleExceptionRetry<TConsumer>(Exception ex, RetryPolicy<TConsumer> retryPolicy, int retryCount, IChannel channel, ILogger<TConsumer> logger) where TConsumer : class
        {
            var maxRetryCount = retryPolicy.MaxRetryCount;

            var remainingRetries = maxRetryCount - retryCount;

            var delay = retryPolicy.RetryInterval;

            if (retryPolicy.ExponentialBackoff && remainingRetries > 0)
            {
                delay = retryPolicy.RetryInterval * (int)Math.Pow(retryPolicy.ExponentialBackoffFactor, remainingRetries);
            }

            logger.LogWarning("[RABBIT-FLOW]: Error processing the message. Retry Count: {retryCount}. Delay: {delay} ms. Exception Message: {message}", retryCount, delay, ex.Message);

            await Task.Delay(delay, CancellationToken.None);

            using var cts = new CancellationTokenSource(channel.ContinuationTimeout);

            return cts.Token;
        }

        private static async Task HandleErrorAsync(bool autoAck, IChannel channel, ulong deliveryTag, CancellationToken cancellationToken)
        {
            if (autoAck)
            {
                await channel.BasicAckAsync(deliveryTag, false, cancellationToken);
            }
            else
            {
                await channel.BasicNackAsync(deliveryTag, false, false, cancellationToken);
            }
        }
    }
}