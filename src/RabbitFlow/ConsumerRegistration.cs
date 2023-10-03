using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitFlow.Services;
using RabbitFlow.Settings;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

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
        public static IApplicationBuilder UseConsumer<TEventType, TConsumer>(this IApplicationBuilder app, Action<ConsumerRegisterSettings>? settings = null)
        {
            var consumer_settings = new ConsumerRegisterSettings();

            settings?.Invoke(consumer_settings);

            if (!consumer_settings.Active)
                return app;

            var scope = app.ApplicationServices.CreateScope();

            var serviceProvider = scope.ServiceProvider;

            var consumerLogger = serviceProvider.GetRequiredService<ILogger<TConsumer>>();

            var factory = serviceProvider.GetRequiredService<ConnectionFactory>() ?? throw new Exception("ConnectionFactory was not found in Service Collection");

            var jsonSerializerOption = serviceProvider.GetService<JsonSerializerOptions>() ?? new JsonSerializerOptions();

            var consumerOptions = serviceProvider.GetRequiredService<ConsumerSettings<TConsumer>>() ?? throw new Exception("ConsummerOptions was not found in Service Collection");

            var retryPolicy = serviceProvider.GetService<RetryPolicy<TConsumer>>() ?? new RetryPolicy<TConsumer> { MaxRetryCount = 1 };

            var consumerType = typeof(TConsumer);

            var consumerAbstraction = consumerType.GetInterfaces().FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IRabbitFlowConsumer<>)) ??
                throw new Exception("Consumer must implement IHvBusConsumer<T>");

            var eventType = consumerAbstraction.GetGenericArguments().First();

            if (eventType != typeof(TEventType))
                throw new Exception($"Consumer must implement IHvBusConsumer<{typeof(TEventType).Name}>");

            var connection = factory.CreateConnection(typeof(TConsumer).Name);

            var channel = connection.CreateModel();

            channel.ContinuationTimeout = consumerOptions.Timeout;

            channel.BasicQos(prefetchSize: 0, prefetchCount: consumerOptions.PrefetchCount, global: false);

            var rabbitConsumer = new EventingBasicConsumer(channel);

            object? consumerService = null;

            if (!consumer_settings.PerMessageInstance)
                consumerService = serviceProvider.GetRequiredService(consumerAbstraction) ?? throw new Exception("Consumer was not found in Service Collection");

            rabbitConsumer.Received += async (sender, args) =>
            {
                using var cancelationSource = new CancellationTokenSource(channel.ContinuationTimeout);

                var cancellationToken = cancelationSource.Token;

                if (consumer_settings.PerMessageInstance)
                    consumerService = serviceProvider.GetRequiredService(consumerAbstraction) ?? throw new Exception("Consumer was not found in Service Collection");

                var retryCount = retryPolicy.MaxRetryCount;

                bool shouldDelay = retryCount > 1;

                string message = string.Empty;

                while (retryCount > 0)
                {
                    try
                    {
                        var body = args.Body.ToArray();

                        message = Encoding.UTF8.GetString(body);

                        var @event = JsonSerializer.Deserialize(message, typeof(TEventType), jsonSerializerOption);

                        await (consumerService as IRabbitFlowConsumer<TEventType>)!.HandleAsync((TEventType)@event!, cancellationToken);

                        if (!consumerOptions.AutoAck)
                        {
                            channel.BasicAck(args.DeliveryTag, false);
                        }

                        break;
                    }
                    catch (TaskCanceledException ex) when (ex.CancellationToken == cancellationToken)
                    {
                        var delay = retryPolicy.ExponentialBackoff && retryCount < retryPolicy.MaxRetryCount ? retryPolicy.RetryInterval * retryPolicy.ExponentialBackoffFactor : retryPolicy.RetryInterval;

                        consumerLogger.LogWarning("Timeout receiving message. Retry Count: {retryCount}. Delay: {delay} ms. Message: {message}", retryCount, delay, message);

                        await Task.Delay(delay, CancellationToken.None);

                        cancellationToken = new CancellationTokenSource(channel.ContinuationTimeout).Token;

                        retryCount--;
                    }
                    catch (TaskCanceledException ex)
                    {
                        consumerLogger.LogError(ex, "Task canceled for an unexpected reason while receiving message. Message: {message}", message);
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (shouldDelay)
                        {
                            var delay = retryPolicy.ExponentialBackoff && retryCount < retryPolicy.MaxRetryCount ? retryPolicy.RetryInterval * retryPolicy.ExponentialBackoffFactor : retryPolicy.RetryInterval;

                            consumerLogger.LogWarning(ex, "Error receiving message. Retry Count: {retry}. Delay: {delay} ms. Message: {message}", retryCount, delay, message);

                            await Task.Delay(delay, CancellationToken.None);

                            cancellationToken = new CancellationTokenSource(channel.ContinuationTimeout).Token;
                        }
                        retryCount--;
                    }
                }

                if (retryCount == 0)
                {
                    consumerLogger.LogError("Error receiving message. Retry Count: {retry}. Message: {message}", retryCount, message);

                    if (!consumerOptions.AutoAck)
                    {
                        channel.BasicNack(args.DeliveryTag, false, false);
                    }
                }
            };

            channel.BasicConsume(queue: consumerOptions.QueueName, autoAck: consumerOptions.AutoAck, consumer: rabbitConsumer);

            return app;
        }
    }
}