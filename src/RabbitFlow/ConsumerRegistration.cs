using EasyRabbitFlow.Exceptions;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EasyRabbitFlow
{
    public static class ConsumerRegistration
    {
        /// <summary>
        /// Registers and starts a RabbitMQ consumer of type <typeparamref name="TConsumer"/> for messages of type <typeparamref name="TEventType"/>.
        /// </summary>
        /// <typeparam name="TEventType">The type of the message payload the consumer handles.</typeparam>
        /// <typeparam name="TConsumer">The consumer implementation that processes incoming messages.</typeparam>
        /// <param name="rootServiceProvider">The root service provider used to resolve dependencies.</param>
        /// <param name="settings">Optional action to configure consumer registration settings.</param>
        /// <param name="cancellationToken">A cancellation token for graceful shutdown.</param>
        /// <returns>The original <see cref="IServiceProvider"/> instance, allowing fluent chaining.</returns>
        /// <remarks>
        /// This method establishes a connection and channel, declares queues and exchanges if configured,
        /// and begins consuming messages asynchronously with retry, DLQ, and error handling support.
        /// </remarks>
        public static async Task<IServiceProvider> InitializeConsumerAsync<TEventType, TConsumer>(
            this IServiceProvider rootServiceProvider,
            Action<ConsumerRegisterSettings>? settings = null,
            CancellationToken cancellationToken = default)
            where TConsumer : class
            where TEventType : class
        {
            var consumerSettings = new ConsumerRegisterSettings();

            settings?.Invoke(consumerSettings);

            if (!consumerSettings.Active)
            {
                return rootServiceProvider;
            }

            var logger = rootServiceProvider.GetRequiredService<ILogger<TConsumer>>();

            var factory = rootServiceProvider.GetRequiredService<ConnectionFactory>()
                          ?? throw new Exception("ConnectionFactory was not found in Service Collection");


            var jsonSerializerOption = rootServiceProvider.GetKeyedService<JsonSerializerOptions>("RabbitFlowJsonSerializer")
                                        ?? JsonSerializerOptions.Web;

            var consumerOptions = rootServiceProvider.GetRequiredService<ConsumerSettings<TConsumer>>()
                                  ?? throw new Exception("ConsummerOptions was not found in Service Collection");

            var retryPolicy = rootServiceProvider.GetService<RetryPolicy<TConsumer>>()
                              ?? new RetryPolicy<TConsumer> { MaxRetryCount = 1 };

            var customDeadletter = rootServiceProvider.GetService<CustomDeadLetterSettings<TConsumer>>();

            var consumerType = typeof(TConsumer);

            var consumerAbstraction = consumerType.GetInterfaces()
                .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IRabbitFlowConsumer<>))
                ?? throw new RabbitFlowException($"[RABBIT-FLOW]: {consumerType.Name} must implement IRabbitFlowConsumer<T> to be registered as a consumer.");

            var eventType = consumerAbstraction.GetGenericArguments().First();

            if (eventType != typeof(TEventType))
            {
                throw new RabbitFlowException($"[RABBIT-FLOW]: {consumerType.Name} must implement IRabbitFlowConsumer<{typeof(TEventType).Name}>.");
            }

            if (string.IsNullOrWhiteSpace(consumerOptions.QueueName))
            {
                throw new RabbitFlowException("[RABBIT-FLOW]: QueueName cannot be null or empty.");
            }

            var connectionId = consumerOptions.ConsumerId ?? consumerOptions.QueueName;

            var connection = await factory.CreateConnectionAsync($"consumer_{connectionId}", cancellationToken);

            var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

            connection.ConnectionShutdownAsync += (sender, args) =>
            {
                logger.LogWarning("[RABBIT-FLOW]: Connection shutdown. ReplyCode: {code}, ReplyText: {text}", args.ReplyCode, args.ReplyText);
                return Task.CompletedTask;
            };

            connection.CallbackExceptionAsync += (sender, args) =>
            {
                logger.LogError(args.Exception, "[RABBIT-FLOW]: Connection callback exception.");
                return Task.CompletedTask;
            };

            connection.RecoverySucceededAsync += (sender, args) =>
            {
                logger.LogInformation("[RABBIT-FLOW]: Connection recovery succeeded.");
                return Task.CompletedTask;
            };

            connection.ConnectionRecoveryErrorAsync += (sender, args) =>
            {
                logger.LogError(args.Exception, "[RABBIT-FLOW]: Connection recovery error.");
                return Task.CompletedTask;
            };

            connection.ConnectionBlockedAsync += (sender, args) =>
            {
                logger.LogWarning("[RABBIT-FLOW]: Connection blocked. Reason: {reason}", args.Reason);
                return Task.CompletedTask;
            };

            connection.ConnectionUnblockedAsync += (sender, args) =>
            {
                logger.LogInformation("[RABBIT-FLOW]: Connection unblocked.");
                return Task.CompletedTask;
            };

            channel.CallbackExceptionAsync += (sender, args) =>
            {
                logger.LogWarning(args.Exception, "[RABBIT-FLOW]: Channel callback exception.");
                return Task.CompletedTask;
            };

            channel.ChannelShutdownAsync += (sender, args) =>
            {
                logger.LogWarning("[RABBIT-FLOW]: Channel shutdown. ReplyCode: {code}, ReplyText: {text}", args.ReplyCode, args.ReplyText);
                return Task.CompletedTask;
            };

            channel.ContinuationTimeout = consumerOptions.Timeout;

            await channel.BasicQosAsync(prefetchSize: 0,
                prefetchCount: consumerOptions.PrefetchCount,
                global: false,
                cancellationToken);

            string deadLetterQueueName = string.Empty;

            if (consumerOptions.AutoGenerate)
            {
                var autoGenerateSettings = rootServiceProvider.GetService<AutoGenerateSettings<TConsumer>>()
                                           ?? new AutoGenerateSettings<TConsumer>();

                var exchangeName = autoGenerateSettings.ExchangeName ?? $"{consumerOptions.QueueName}-exchange";
                
                var queueName = consumerOptions.QueueName;
                
                var args = autoGenerateSettings.Args;
                
                var routingKey = autoGenerateSettings.RoutingKey ?? $"{consumerOptions.QueueName}-routing-key";

                if (autoGenerateSettings.GenerateDeadletterQueue)
                {
                    deadLetterQueueName = $"{queueName}-deadletter";
                    
                    var deadLetterExchange = $"{queueName}-deadletter-exchange";
                    
                    var deadLetterRoutingKey = $"{queueName}-deadletter-routing-key";

                    await channel.QueueDeclareAsync(deadLetterQueueName,
                        durable: autoGenerateSettings.DurableQueue,
                        exclusive: false,
                        autoDelete: autoGenerateSettings.AutoDeleteQueue,
                        arguments: null,
                        cancellationToken: cancellationToken);

                    await channel.ExchangeDeclareAsync(deadLetterExchange, "direct",
                        durable: autoGenerateSettings.DurableExchange,
                        cancellationToken: cancellationToken);

                    await channel.QueueBindAsync(deadLetterQueueName, deadLetterExchange, deadLetterRoutingKey);

                    args ??= new Dictionary<string, object?>();
                    
                    args["x-dead-letter-exchange"] = deadLetterExchange;
                    
                    args["x-dead-letter-routing-key"] = deadLetterRoutingKey;
                }

                await channel.QueueDeclareAsync(queueName,
                    durable: autoGenerateSettings.DurableQueue,
                    exclusive: autoGenerateSettings.ExclusiveQueue,
                    autoDelete: autoGenerateSettings.AutoDeleteQueue,
                    arguments: args);

                if (autoGenerateSettings.GenerateExchange)
                {
                    await channel.ExchangeDeclareAsync(exchangeName,
                        type: autoGenerateSettings.ExchangeType.ToString().ToLowerInvariant(),
                        durable: autoGenerateSettings.DurableExchange);

                    await channel.QueueBindAsync(queueName, exchangeName, routingKey);
                }
            }

            var consumer = new AsyncEventingBasicConsumer(channel);

            var semaphore = new SemaphoreSlim(consumerOptions.PrefetchCount);

            consumer.ReceivedAsync += async (sender, args) =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await semaphore.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                _ = ProcessMessageAsync(args);
            };

            async Task ProcessMessageAsync(BasicDeliverEventArgs args)
            {
                try
                {
                    var body = args.Body.ToArray();

                    var message = Encoding.UTF8.GetString(body);

                    TEventType? @event;

                    try
                    {
                        @event = JsonSerializer.Deserialize<TEventType>(message, jsonSerializerOption)
                                 ?? throw new RabbitFlowException("[RABBIT-FLOW]: Message deserialization returned null.");
                    }
                    catch (Exception ex)
                    {
                        await HandleErrorAsync<TEventType>(
                            autoAck: consumerOptions.AutoAckOnError,
                            channel: channel,
                            deliveryTag: args.DeliveryTag,
                            extendDeadletterMessage: consumerOptions.ExtendDeadletterMessage,
                            deadletterQueue: deadLetterQueueName,
                            @event: null,
                            exception: ex,
                            serializerOptions: jsonSerializerOption,
                            cancellationToken: cancellationToken);

                        logger.LogError(ex, "[RABBIT-FLOW]: Deserialization failed.");

                        return;
                    }

                    Exception? lastException = null;

                    var remainingAttempts = retryPolicy.MaxRetryCount;

                    while (remainingAttempts > 0 && !cancellationToken.IsCancellationRequested)
                    {
                        using var attemptCts = new CancellationTokenSource(channel.ContinuationTimeout);

                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(attemptCts.Token, cancellationToken);

                        var attemptCt = linked.Token;

                        IServiceScope? scope = null;

                        TConsumer? consumerService = null;

                        try
                        {
                            scope = rootServiceProvider.CreateScope();
                            
                            consumerService = scope.ServiceProvider.GetRequiredService<TConsumer>();

                            if (!(consumerService is IRabbitFlowConsumer<TEventType> typedConsumer))
                                throw new RabbitFlowException($"[RABBIT-FLOW]: Consumer does not implement IRabbitFlowConsumer<{typeof(TEventType).Name}>.");

                            await typedConsumer.HandleAsync(@event, attemptCt).ConfigureAwait(false);

                            await SafeAckAsync(channel, args.DeliveryTag, logger, cancellationToken);

                            return; // success
                        }
                        catch (OperationCanceledException ex) when (attemptCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                        {
                            lastException = ex;
                            await HandleExceptionRetry(ex, retryPolicy, remainingAttempts, logger, cancellationToken);
                        }
                        catch (TaskCanceledException ex) when (attemptCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                        {
                            lastException = ex;
                            await HandleExceptionRetry(ex, retryPolicy, remainingAttempts, logger, cancellationToken);
                        }
                        catch (RabbitFlowTransientException ex)
                        {
                            lastException = ex;
                            await HandleExceptionRetry(ex, retryPolicy, remainingAttempts, logger, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            await HandleErrorAsync(
                                consumerOptions.AutoAckOnError,
                                channel,
                                args.DeliveryTag,
                                consumerOptions.ExtendDeadletterMessage,
                                deadLetterQueueName,
                                @event,
                                ex,
                                jsonSerializerOption,
                                cancellationToken);
                            logger.LogError(ex, "[RABBIT-FLOW]: Non-transient error. No more retries.");
                            return;
                        }
                        finally
                        {
                            scope?.Dispose();
                            remainingAttempts--;
                        }
                    }

                    if (remainingAttempts == 0)
                    {
                        var overRetryEx = new RabbitFlowOverRetriesException(retryPolicy.MaxRetryCount, lastException);

                        await HandleErrorAsync(
                            consumerOptions.AutoAckOnError,
                            channel,
                            args.DeliveryTag,
                            consumerOptions.ExtendDeadletterMessage,
                            deadLetterQueueName,
                            @event,
                            overRetryEx,
                            jsonSerializerOption,
                            cancellationToken);

                        logger.LogError(overRetryEx,
                            "[RABBIT-FLOW]: Exhausted retries ({Max}) for message.", retryPolicy.MaxRetryCount);

                        if (customDeadletter != null && @event != null)
                        {
                            try
                            {
                                var publisher = rootServiceProvider.GetRequiredService<IRabbitFlowPublisher>();
                                await publisher.PublishAsync(@event, customDeadletter.DeadletterQueueName, publisherId: "custom-dead-letter");
                            }
                            catch (Exception dlxEx)
                            {
                                logger.LogError(dlxEx, "[RABBIT-FLOW]: Failed publishing to custom dead-letter queue.");
                            }
                        }
                    }
                }
                catch (AlreadyClosedException exClosed)
                {
                    // Channel/Connection closed: broker will requeue if message was not ACKed.
                    logger.LogWarning(exClosed, "[RABBIT-FLOW]: Channel closed during message processing. Message likely requeued.");
                }
                catch (Exception exOuter)
                {
                    logger.LogError(exOuter, "[RABBIT-FLOW]: Unhandled exception in message processing.");
                }
                finally
                {
                    semaphore.Release();
                }
            }

            await channel.BasicConsumeAsync(queue: consumerOptions.QueueName,
                autoAck: false,
                consumer: consumer,
                cancellationToken);

            return rootServiceProvider;
        }

        private static async Task SafeAckAsync<TConsumer>(IChannel channel, ulong deliveryTag, ILogger<TConsumer> logger, CancellationToken ct)
        {
            try
            {
                await channel.BasicAckAsync(deliveryTag, false, ct);
            }
            catch (AlreadyClosedException ex)
            {
                logger.LogWarning(ex, "[RABBIT-FLOW]: Channel closed before ACK. Message will be requeued by broker.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[RABBIT-FLOW]: Unexpected error while ACKing message.");
            }
        }

        private static async Task HandleExceptionRetry<TConsumer>(
            Exception ex,
            RetryPolicy<TConsumer> retryPolicy,
            int remainingAttempts,
            ILogger<TConsumer> logger,
            CancellationToken ct) where TConsumer : class
        {
            var attemptIndex = retryPolicy.MaxRetryCount - remainingAttempts + 1;

            var baseDelay = retryPolicy.RetryInterval;

            long delay = baseDelay;

            if (retryPolicy.ExponentialBackoff && attemptIndex > 1)
            {
                try
                {
                    var computed = checked(baseDelay * (long)Math.Pow(retryPolicy.ExponentialBackoffFactor, attemptIndex - 1));

                    delay = Math.Min(computed, 60_000);
                }
                catch (OverflowException)
                {
                    delay = 60_000;
                }
            }

            logger.LogWarning("[RABBIT-FLOW]: Attempt {Attempt}/{Max}. Waiting {Delay} ms. Exception: {Message}", attemptIndex, retryPolicy.MaxRetryCount, delay, ex.Message);

            try
            {
                await Task.Delay((int)delay, ct);
            }
            catch (OperationCanceledException)
            {
                // do nothing, cancellation requested
            }
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
                try
                {
                    await channel.BasicAckAsync(deliveryTag, false, cancellationToken);
                }
                catch (AlreadyClosedException) { /* back to queue */ }

                return;
            }

            if (extendDeadletterMessage && !string.IsNullOrEmpty(deadletterQueue))
            {
                var innerExceptions = new List<object>();
                
                const int MaxDepth = 10;
                
                var temp = exception;
                
                var depth = 0;
                
                while (temp?.InnerException != null && depth < MaxDepth)
                {
                    temp = temp.InnerException;
                    
                    if (temp != null)
                    {
                        innerExceptions.Add(new
                        {
                            exceptionType = temp.GetType().Name,
                            errorMessage = temp.Message,
                            stackTrace = temp.StackTrace,
                            source = temp.Source
                        });
                    }
                    
                    depth++;
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
                    innerExceptions
                };

                try
                {
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(extendedMessage, serializerOptions));

                    await channel.BasicPublishAsync(exchange: "",
                        routingKey: deadletterQueue,
                        body: bytes,
                        cancellationToken: cancellationToken);

                    await channel.BasicAckAsync(deliveryTag, false, cancellationToken);
                }
                catch (AlreadyClosedException)
                {
                    // If the channel is closed here, the message is requeued (no ack) and the extended payload is not published.
                }
            }
            else
            {
                try
                {
                    await channel.BasicNackAsync(deliveryTag, false, false, cancellationToken);
                }
                catch (AlreadyClosedException)
                {
                    // Closed channel. Broker automatically requeues (similar to no explicit ack).
                }
            }
        }
    }
}