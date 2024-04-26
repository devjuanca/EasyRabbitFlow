using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        /// Registers a RabbitMQ consumer for a specific event type and consumer type.
        /// </summary>
        /// <typeparam name="TEventType">The type of the event to consume.</typeparam>
        /// <typeparam name="TConsumer">The type of the consumer.</typeparam>
        /// <param name="app">The application builder.</param>
        /// <param name="settings">Options to configure Consumer.</param>
        /// <returns>The modified application builder.</returns>
        public static IApplicationBuilder UseConsumer<TEventType, TConsumer>(this IApplicationBuilder app, Action<ConsumerRegisterSettings>? settings = null) where TConsumer : class where TEventType : class
        {
            var consumer_settings = new ConsumerRegisterSettings();

            settings?.Invoke(consumer_settings);

            if (!consumer_settings.Active)
                return app;

            var appServiceProvider = app.ApplicationServices;

            var consumerLogger = appServiceProvider.GetRequiredService<ILogger<TConsumer>>();

            var factory = appServiceProvider.GetRequiredService<ConnectionFactory>() ?? throw new Exception("ConnectionFactory was not found in Service Collection");

            var jsonSerializerOption = appServiceProvider.GetService<JsonSerializerOptions>() ?? new JsonSerializerOptions();

            var consumerOptions = appServiceProvider.GetRequiredService<ConsumerSettings<TConsumer>>() ?? throw new Exception("ConsummerOptions was not found in Service Collection");

            var retryPolicy = appServiceProvider.GetService<RetryPolicy<TConsumer>>() ?? new RetryPolicy<TConsumer> { MaxRetryCount = 1 };

            var customDeadletter = appServiceProvider.GetService<CustomDeadLetterSettings<TConsumer>>() ?? null;

            var consumerType = typeof(TConsumer);

            var consumerAbstraction = consumerType.GetInterfaces().FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IRabbitFlowConsumer<>)) ??
                throw new Exception("Consumer must implement IRabbitFlowConsumer<T>");

            var eventType = consumerAbstraction.GetGenericArguments().First();

            if (eventType != typeof(TEventType))
                throw new Exception($"Consumer must implement IRabbitFlowConsumer<{typeof(TEventType).Name}>");

            var connection = factory.CreateConnection(typeof(TConsumer).Name);

            var channel = connection.CreateModel();

            channel.ContinuationTimeout = consumerOptions.Timeout;

            channel.BasicQos(prefetchSize: 0, prefetchCount: consumerOptions.PrefetchCount, global: false);

            if (consumerOptions.AutoGenerate)
            {
                var autoGenerateSettings = appServiceProvider.GetService<AutoGenerateSettings<TConsumer>>() ?? new AutoGenerateSettings<TConsumer>();

                var exchangeName = autoGenerateSettings.ExchangeName ?? $"{consumerOptions.QueueName}-exchange";

                var queueName = consumerOptions.QueueName;

                var args = autoGenerateSettings.Args;

                var routingKey = autoGenerateSettings.RoutingKey ?? $"{consumerOptions.QueueName}-routing-key";

                if (autoGenerateSettings.GenerateDeadletterQueue)
                {
                    var deadLetterQueueName = $"{queueName}-deadletter";

                    var deadLetterExchange = $"{queueName}-deadletter-exchange";

                    var deadLetterRoutingKey = $"{queueName}-deadletter-routing-key";

                    channel.QueueDeclare(queue: deadLetterQueueName, durable: true, exclusive: false, autoDelete: false, arguments: null);

                    channel.ExchangeDeclare(exchange: deadLetterExchange, type: "direct", durable: true);

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
                channel.QueueDeclare(queue: queueName, durable: autoGenerateSettings.DurableQueue, exclusive: autoGenerateSettings.ExclusiveQueue, autoDelete: autoGenerateSettings.AutoDeleteQueue, arguments: args);

                if (autoGenerateSettings.GenerateExchange)
                {
                    // Declare exchange
                    channel.ExchangeDeclare(exchange: exchangeName, type: autoGenerateSettings.ExchangeType.ToString().ToLower(), durable: autoGenerateSettings.DurableExchange);

                    // Bind queue to exchange
                    channel.QueueBind(queue: queueName, exchange: exchangeName, routingKey: routingKey);
                }
            }
            var rabbitConsumer = new EventingBasicConsumer(channel);

            object? consumerService = null;

            rabbitConsumer.Received += async (sender, args) =>
            {
                IServiceScope scope = default!;

                using var cancelationSource = new CancellationTokenSource(channel.ContinuationTimeout);

                var cancellationToken = cancelationSource.Token;

                try
                {
                    if (consumer_settings.PerMessageInstance)
                    {
                        scope = app.ApplicationServices.CreateScope();

                        var scopedServiceProvider = scope.ServiceProvider;

                        consumerService = scopedServiceProvider.GetRequiredService(consumerAbstraction) ?? throw new Exception("Consumer was not found in Service Collection");
                    }
                    else
                    {
                        consumerService = appServiceProvider.GetRequiredService(consumerAbstraction) ?? throw new Exception("Consumer was not found in Service Collection");
                    }
                }
                catch (InvalidOperationException ex)
                {
                    throw new Exception("Ensure all services used in the consumer are Singleton when PerMessageInstance is set to false. Alternatively, set PerMessageInstance to true, which is the default behavior.", ex);
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
                            consumerLogger.LogError("Failed to serialize the message. Check the message format or serialization settings.");
                            break;
                        }

                        if (!(consumerService is IRabbitFlowConsumer<TEventType>))
                        {
                            throw new Exception("Consumer service must implement IRabbitFlowConsumer<TEventType>. Ensure that all consumer services adhere to this interface to maintain the flow. This exception should never be thrown under normal circumstances.");
                        }

                        await (consumerService as IRabbitFlowConsumer<TEventType>)!.HandleAsync((TEventType)@event, cancellationToken);

                        channel.BasicAck(args.DeliveryTag, false);

                        break;
                    }
                    catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken)
                    {
                        cancellationToken = await HandleExceptionRetry(ex, retryPolicy, consumerLogger, retryCount, channel, message);

                        retryCount--;
                    }
                    catch (TaskCanceledException ex) when (ex.CancellationToken == cancellationToken)
                    {
                        cancellationToken = await HandleExceptionRetry(ex, retryPolicy, consumerLogger, retryCount, channel, message);

                        retryCount--;
                    }
                    catch (TaskCanceledException ex)
                    {
                        consumerLogger.LogError(ex, "Task canceled for an unexpected reason while receiving message. Message: {message}", message);
                        break;
                    }
                    catch (OperationCanceledException ex)
                    {
                        consumerLogger.LogError(ex, "Task canceled for an unexpected reason while receiving message. Message: {message}", message);
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (shouldDelay)
                        {
                            cancellationToken = await HandleExceptionRetry(ex, retryPolicy, consumerLogger, retryCount, channel, message);
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
                    consumerLogger.LogError("Error receiving message. Retry Count: {retry}. Message: {message}", retryCount, message);

                    if (consumerOptions.AutoAckOnError)
                    {
                        channel.BasicAck(args.DeliveryTag, false);

                    }
                    else
                    {
                        channel.BasicNack(args.DeliveryTag, false, false);
                    }

                    if (customDeadletter != null)
                    {
                        var publisher = appServiceProvider.GetRequiredService<IRabbitFlowPublisher>();

                        if (@event != null)
                            await publisher.PublishAsync<TEventType>((TEventType)@event, "", customDeadletter.DeadletterQueueName, false);

                    }
                }
            };

            channel.BasicConsume(queue: consumerOptions.QueueName, autoAck: false, consumer: rabbitConsumer);

            return app;
        }

        public static async Task<CancellationToken> HandleExceptionRetry<TConsumer>(Exception ex, RetryPolicy<TConsumer> retryPolicy, ILogger consumerLogger, int retryCount, IModel channel, string message) where TConsumer : class
        {
            var delay = retryPolicy.ExponentialBackoff && retryCount < retryPolicy.MaxRetryCount ? retryPolicy.RetryInterval * retryPolicy.ExponentialBackoffFactor : retryPolicy.RetryInterval;

            consumerLogger.LogWarning(ex, "Timeout receiving message. Retry Count: {retryCount}. Delay: {delay} ms. Message: {message}", retryCount, delay, message);

            await Task.Delay(delay, CancellationToken.None);

            var cancellationToken = new CancellationTokenSource(channel.ContinuationTimeout).Token;

            return cancellationToken;
        }
    }

}