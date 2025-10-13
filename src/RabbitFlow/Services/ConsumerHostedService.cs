using EasyRabbitFlow.Settings;
using EasyRabbitFlow.Exceptions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
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

namespace EasyRabbitFlow.Services
{
    internal class ConsumerHostedService : IHostedService
    {
        private readonly IServiceProvider _root;

        private readonly ILogger<ConsumerHostedService> _logger;

        private readonly List<IConnection> _connections = new List<IConnection>();

        private readonly List<IChannel> _channels = new List<IChannel>();

        public ConsumerHostedService(IServiceProvider root, ILogger<ConsumerHostedService> logger)
        {
            _root = root;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var markers = _root.GetServices<IConsumerSettingsMarker>().ToList();

            if (markers.Count == 0)
            {
                _logger.LogInformation("[RABBIT-FLOW]: No consumers found.");
                return;
            }

            var connectionFactory = _root.GetRequiredService<ConnectionFactory>();

            var serializerOptions = _root.GetKeyedService<JsonSerializerOptions>("RabbitFlowJsonSerializer") ?? new JsonSerializerOptions 
            { 
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            foreach (var marker in markers)
            {
                var settingsObj = marker.SettingsInstance;

                if (!settingsObj.Enable)
                {
                    _logger.LogInformation("[RABBIT-FLOW]: Consumer for {Consumer} is disabled. Skipping.", marker.ConsumerType.Name);
                    continue;
                }

                var consumerType = marker.ConsumerType;
                
                var eventType = marker.EventType;
                
                var queueName = settingsObj.QueueName;

                if (string.IsNullOrWhiteSpace(queueName))
                {
                    _logger.LogWarning("[RABBIT-FLOW]: Empty QueueName for {Consumer}. Avoiding.", consumerType.Name);
                    continue;
                }

                bool autoGenerate = settingsObj.AutoGenerate;
                
                ushort prefetch = settingsObj.PrefetchCount;
                
                TimeSpan timeout = settingsObj.Timeout;
                
                bool extendDeadLetter = settingsObj.ExtendDeadletterMessage;
                
                bool autoAckOnError = settingsObj.AutoAckOnError;
                
                string consumerId = settingsObj.ConsumerId ?? queueName;

                var retryPolicyObj = _root.GetService(typeof(RetryPolicy<>).MakeGenericType(consumerType))
                                       ?? Activator.CreateInstance(typeof(RetryPolicy<>).MakeGenericType(consumerType));

                // Extract retry configuration once (avoid per-message reflection)
                var rpType = retryPolicyObj.GetType();

                int rpMax = (int)rpType.GetProperty(nameof(RetryPolicy<object>.MaxRetryCount))!.GetValue(retryPolicyObj)!;

                int rpInterval = (int)rpType.GetProperty(nameof(RetryPolicy<object>.RetryInterval))!.GetValue(retryPolicyObj)!;

                bool rpExp = (bool)rpType.GetProperty(nameof(RetryPolicy<object>.ExponentialBackoff))!.GetValue(retryPolicyObj)!;

                int rpFactor = (int)rpType.GetProperty(nameof(RetryPolicy<object>.ExponentialBackoffFactor))!.GetValue(retryPolicyObj)!;

                var customDeadLetterObj = _root.GetService(typeof(CustomDeadLetterSettings<>).MakeGenericType(consumerType));

                var connection = await connectionFactory.CreateConnectionAsync($"consumer_{consumerId}", cancellationToken);
                
                var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
                
                _connections.Add(connection);
                
                _channels.Add(channel);

                channel.ContinuationTimeout = timeout;
                
                await channel.BasicQosAsync(0, prefetch, false, cancellationToken);

                string deadLetterQueueName = string.Empty;

                if (autoGenerate)
                {
                    var autoGenObj = _root.GetService(typeof(AutoGenerateSettings<>).MakeGenericType(consumerType));

                    if (autoGenObj != null)
                    {
                        var exchangeName = (string?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.ExchangeName))?.GetValue(autoGenObj) ?? $"{queueName}-exchange";
                        
                        var routingKey = (string?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.RoutingKey))?.GetValue(autoGenObj) ?? $"{queueName}-routing-key";
                        
                        var generateDeadletter = (bool?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.GenerateDeadletterQueue))?.GetValue(autoGenObj) ?? false;
                        
                        var durableQueue = (bool?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.DurableQueue))?.GetValue(autoGenObj) ?? true;
                        
                        var durableExchange = (bool?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.DurableExchange))?.GetValue(autoGenObj) ?? true;
                        
                        var autoDeleteQueue = (bool?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.AutoDeleteQueue))?.GetValue(autoGenObj) ?? false;
                        
                        var exclusiveQueue = (bool?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.ExclusiveQueue))?.GetValue(autoGenObj) ?? false;
                        
                        var exchangeType = autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.ExchangeType))?.GetValue(autoGenObj)?.ToString()?.ToLowerInvariant() ?? "direct";
                        
                        var generateExchange = (bool?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.GenerateExchange))?.GetValue(autoGenObj) ?? false;

                        IDictionary<string, object?>? args = autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.Args))?.GetValue(autoGenObj) is IDictionary<string, object?> argsVal ? new Dictionary<string, object?>(argsVal) : null;

                        if (generateDeadletter)
                        {
                            deadLetterQueueName = $"{queueName}-deadletter";
                            
                            var deadLetterExchange = $"{queueName}-deadletter-exchange";
                            
                            var deadLetterRoutingKey = $"{queueName}-deadletter-routing-key";

                            await channel.QueueDeclareAsync(deadLetterQueueName, durableQueue, false, autoDeleteQueue, null, cancellationToken: cancellationToken);
                            
                            await channel.ExchangeDeclareAsync(deadLetterExchange, "direct", durableExchange, cancellationToken: cancellationToken);
                            
                            await channel.QueueBindAsync(deadLetterQueueName, deadLetterExchange, deadLetterRoutingKey);

                            args ??= new Dictionary<string, object?>();
                            
                            args["x-dead-letter-exchange"] = deadLetterExchange;
                            
                            args["x-dead-letter-routing-key"] = deadLetterRoutingKey;
                        }

                        await channel.QueueDeclareAsync(queueName, durableQueue, exclusiveQueue, autoDeleteQueue, args);
                        
                        if (generateExchange)
                        {
                            await channel.ExchangeDeclareAsync(exchangeName, exchangeType, durableExchange);

                            await channel.QueueBindAsync(queueName, exchangeName, routingKey);
                        }
                    }
                }

                var consumer = new AsyncEventingBasicConsumer(channel);
                
                var semaphore = new SemaphoreSlim(prefetch);

                // Build fast processor delegate using precompiled factory
                var markerFactory = marker.Factory;
                
                var processor = BuildProcessorFromFactory(markerFactory, consumerType, eventType, channel, deadLetterQueueName, extendDeadLetter, autoAckOnError, serializerOptions, semaphore, rpMax, rpInterval, rpExp, rpFactor, customDeadLetterObj);

                consumer.ReceivedAsync += async (_, ea) =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    try 
                    { 
                        await semaphore.WaitAsync(cancellationToken); 
                    } 
                    catch { return; }

                    _ = processor(ea, cancellationToken);
                };

                await channel.BasicConsumeAsync(queue: queueName, autoAck: false, consumer: consumer, cancellationToken: cancellationToken);
            }
        }

        private Func<BasicDeliverEventArgs, CancellationToken, Task> BuildProcessorFromFactory(
            ConsumerSettingsFactory markerFactory,
            Type consumerType,
            Type eventType,
            IChannel channel,
            string deadLetterQueueName,
            bool extendDeadLetter,
            bool autoAckOnError,
            JsonSerializerOptions serializerOptions,
            SemaphoreSlim semaphore,
            int maxRetries,
            int retryInterval,
            bool exponential,
            int expFactor,
            object? customDeadLetterObj)
        {
            return async (args, rootCt) =>
            {
                try
                {
                    var body = args.Body.ToArray();

                    object? evt;
                    try
                    {
                        evt = markerFactory.Deserialize(body, serializerOptions) ?? throw new Exception("Deserialization returned null");
                    }
                    catch (Exception ex)
                    {
                        await HandleErrorAsync(channel, args.DeliveryTag, autoAckOnError, extendDeadLetter, deadLetterQueueName, null, ex, eventType, serializerOptions, rootCt);
                        return;
                    }

                    int remainingAttempts = maxRetries;
                    
                    Exception? lastException = null;

                    while (remainingAttempts > 0 && !rootCt.IsCancellationRequested)
                    {
                        using var attemptCts = new CancellationTokenSource(channel.ContinuationTimeout);
                        
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(attemptCts.Token, rootCt);
                        
                        var attemptCt = linked.Token;
                        
                        using var scope = _root.CreateScope();
                        
                        try
                        {
                            var consumer = scope.ServiceProvider.GetRequiredService(consumerType);
                            
                            await markerFactory.InvokeHandleAsync(consumer, evt, attemptCt).ConfigureAwait(false);
                            
                            await SafeAckAsync(channel, args.DeliveryTag, rootCt);
                            
                            return;
                        }
                        catch (OperationCanceledException ex) when (attemptCts.IsCancellationRequested && !rootCt.IsCancellationRequested)
                        {
                            lastException = ex;
                            
                            await ApplyRetryDelayReflection(retryInterval, exponential, expFactor, maxRetries, remainingAttempts, rootCt);
                        }
                        catch (RabbitFlowTransientException ex)
                        {
                            lastException = ex;
                            
                            await ApplyRetryDelayReflection(retryInterval, exponential, expFactor, maxRetries, remainingAttempts, rootCt);
                        }
                        catch (Exception ex)
                        {
                            lastException = ex;
                            
                            remainingAttempts = 0;
                        }
                        finally
                        {
                            remainingAttempts--;
                        }
                    }

                    if (remainingAttempts <= 0)
                    {
                        await HandleErrorAsync(channel, args.DeliveryTag, autoAckOnError, extendDeadLetter, deadLetterQueueName, evt, lastException ?? new Exception("Error desconocido"), eventType, serializerOptions, rootCt);
                        
                        if (customDeadLetterObj != null && evt != null)
                        {
                            try
                            {
                                var queueProp = customDeadLetterObj.GetType().GetProperty("DeadletterQueueName");
                                
                                var deadQueue = queueProp?.GetValue(customDeadLetterObj) as string;
                                
                                if (!string.IsNullOrWhiteSpace(deadQueue) && markerFactory.PublishToCustomDeadletter != null)
                                {
                                    await markerFactory.PublishToCustomDeadletter(evt, deadQueue!, _root);
                                }
                            }
                            catch (Exception dlxEx)
                            {
                                _logger.LogError(dlxEx, "[RABBIT-FLOW]: Error publicando en custom dead-letter.");
                            }
                        }
                    }
                }
                catch (AlreadyClosedException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[RABBIT-FLOW]: Excepción no controlada en procesamiento de mensaje (HostedService).");
                }
                finally
                {
                    semaphore.Release();
                }
            };
        }

        private static async Task ApplyRetryDelayReflection(int baseInterval, bool exponential, int expFactor, int maxRetries, int remainingAttempts, CancellationToken ct)
        {
            if (remainingAttempts <= 1)
            {
                return;
            }
            int attemptIndex = maxRetries - remainingAttempts + 1;
            
            long delay = baseInterval;
            
            if (exponential && attemptIndex > 1)
            {
                try
                {
                    var computed = checked(baseInterval * (long)Math.Pow(expFactor, attemptIndex - 1));
                    delay = Math.Min(computed, 60_000);
                }
                catch { delay = 60_000; }
            }
            try 
            { 
                await Task.Delay((int)delay, ct); 
            } 
            catch { }
        }

        private static async Task SafeAckAsync(IChannel channel, ulong deliveryTag, CancellationToken ct)
        {
            try { await channel.BasicAckAsync(deliveryTag, false, ct); } catch { }
        }

        private static async Task HandleErrorAsync(IChannel channel, ulong deliveryTag, bool autoAck, bool extendDeadletterMessage, string deadletterQueue, object? evt, Exception exception, Type evtType, JsonSerializerOptions serializerOptions, CancellationToken ct)
        {
            if (autoAck)
            {
                try { await channel.BasicAckAsync(deliveryTag, false, ct); } catch { }
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
                        innerExceptions.Add(new { exceptionType = temp.GetType().Name, errorMessage = temp.Message, stackTrace = temp.StackTrace, source = temp.Source });
                    }
                    depth++;
                }
                
                var extendedMessage = new
                {
                    dateUtc = DateTime.UtcNow,
                    messageType = evtType.Name,
                    messageData = evt,
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
                        cancellationToken: ct);
                    
                    await channel.BasicAckAsync(deliveryTag, false, ct);
                }
                catch { }
            }
            else
            {
                try { await channel.BasicNackAsync(deliveryTag, false, false, ct); } catch { }
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            foreach (var channel in _channels)
            {
                try 
                { 
                    channel.Dispose(); 
                } 
                catch { }
            }

            foreach (var conn in _connections)
            {
                try 
                { 
                    conn.Dispose(); 
                } 
                catch { }
            }

            return Task.CompletedTask;
        }
    }
}