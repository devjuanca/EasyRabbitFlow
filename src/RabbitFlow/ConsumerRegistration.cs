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
        /// Registers and starts a RabbitMQ consumer of type <typeparamref name="TConsumer"/> 
        /// for messages of type <typeparamref name="TEventType"/>.
        /// </summary>
        /// <typeparam name="TEventType">The type of message payload that the consumer processes.</typeparam>
        /// <typeparam name="TConsumer">The consumer implementation responsible for handling the messages.</typeparam>
        /// <param name="rootServiceProvider">The root <see cref="IServiceProvider"/> used to resolve dependencies.</param>
        /// <param name="settings">Optional configuration delegate for consumer registration behavior.</param>
        /// <param name="cancellationToken">A token that propagates cancellation requests for graceful shutdown.</param>
        /// <returns>The same <see cref="IServiceProvider"/> instance, allowing fluent chaining.</returns>
        /// <remarks>
        /// This method manually initializes and starts a consumer:
        /// <list type="bullet">
        /// <item><description>Establishes a RabbitMQ connection and channel.</description></item>
        /// <item><description>Declares queues and exchanges (if <c>AutoGenerate</c> is enabled).</description></item>
        /// <item><description>Starts consuming messages asynchronously with built-in retry, DLQ, and error-handling logic.</description></item>
        /// </list>
        /// </remarks>
        /// <remarks>
        /// <b>Deprecated:</b> Use the hosted service registered via <c>AddRabbitFlowConsumers()</c> 
        /// to automatically initialize all registered consumers.
        /// <para>
        /// <b>Migration guide:</b><br/>
        /// 1) Remove manual calls to <c>InitializeConsumerAsync</c> from your <c>Program.cs</c> or Startup file.<br/>
        /// 2) Add <c>services.AddRabbitFlowConsumersHostedService()</c> after configuring RabbitFlow.<br/>
        /// 3) (Optional) Use the <paramref name="settings"/> action to mark consumers as active or inactive if you
        /// still rely on this method during transition.
        /// </para>
        /// This method remains available temporarily for backward compatibility.
        /// </remarks>
        [Obsolete("Use AddRabbitFlowConsumers on IServiceCollection to automatically initialize all consumers. Remove manual calls to InitializeConsumerAsync.")]

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

            var channelGate = new SemaphoreSlim(1, 1);

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
                    var message = Encoding.UTF8.GetString(args.Body.Span);

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
                            channelGate: channelGate,
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

                            var messageContext = new RabbitFlowMessageContext(
                                args.BasicProperties?.MessageId,
                                args.BasicProperties?.CorrelationId,
                                args.Exchange,
                                args.RoutingKey,
                                args.BasicProperties?.Headers,
                                args.DeliveryTag,
                                args.Redelivered);

                            consumerService = scope.ServiceProvider.GetRequiredService<TConsumer>();

                            if (!(consumerService is IRabbitFlowConsumer<TEventType> typedConsumer))
                                throw new RabbitFlowException($"[RABBIT-FLOW]: Consumer does not implement IRabbitFlowConsumer<{typeof(TEventType).Name}>.");

                            await typedConsumer.HandleAsync(@event, messageContext, attemptCt).ConfigureAwait(false);

                            await SafeAckAsync(channel, channelGate, args.DeliveryTag, logger, cancellationToken);

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
                                channelGate,
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
                            channelGate,
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

        private static async Task SafeAckAsync<TConsumer>(IChannel channel, SemaphoreSlim channelGate, ulong deliveryTag, ILogger<TConsumer> logger, CancellationToken ct)
        {
            await channelGate.WaitAsync(ct).ConfigureAwait(false);
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
            finally
            {
                channelGate.Release();
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

                    delay = Math.Min(computed, retryPolicy.MaxRetryDelay);
                }
                catch (OverflowException)
                {
                    delay = retryPolicy.MaxRetryDelay;
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
            SemaphoreSlim channelGate,
            ulong deliveryTag,
            bool extendDeadletterMessage = false,
            string deadletterQueue = "",
            TEventType? @event = null,
            Exception? exception = null,
            JsonSerializerOptions? serializerOptions = null,
            CancellationToken cancellationToken = default) where TEventType : class
        {
            await channelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
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
            finally
            {
                channelGate.Release();
            }
        }
    }
}