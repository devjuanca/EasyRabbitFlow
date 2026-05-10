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

        private readonly List<ConsumerInstance> _instances = new List<ConsumerInstance>();

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

            var serializerOptions = _root.GetKeyedService<JsonSerializerOptions>("RabbitFlowJsonSerializer") ?? JsonSerializerOptions.Web;

            foreach (var marker in markers)
            {
                var settingsObj = marker.SettingsInstance;

                if (!settingsObj.Enable)
                {
                    _logger.LogInformation("[RABBIT-FLOW]: Consumer for {Consumer} is disabled. Skipping.", marker.ConsumerType.Name);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(settingsObj.QueueName))
                {
                    _logger.LogWarning("[RABBIT-FLOW]: Empty QueueName for {Consumer}. Avoiding.", marker.ConsumerType.Name);
                    continue;
                }

                var instance = new ConsumerInstance(_root, _logger, connectionFactory, serializerOptions, marker);

                _instances.Add(instance);

                await instance.StartAsync(cancellationToken);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            foreach (var instance in _instances)
            {
                await instance.StopAsync();
            }
        }
    }

    internal sealed class ConsumerInstance
    {
        private readonly IServiceProvider _root;
        private readonly ILogger _logger;
        private readonly ConnectionFactory _connectionFactory;
        private readonly JsonSerializerOptions _serializerOptions;
        private readonly IConsumerSettingsMarker _marker;
        private readonly ConsumerSettingsFactory _markerFactory;
        private readonly Type _consumerType;
        private readonly Type _eventType;
        private readonly string _queueName;
        private readonly string _consumerId;
        private readonly TimeSpan _timeout;
        private readonly ushort _prefetch;
        private readonly bool _autoGenerate;
        private readonly bool _extendDeadLetter;
        private readonly bool _autoAckOnError;
        private readonly bool _unwrapEnvelopes;
        private readonly int _rpMax;
        private readonly int _totalAttempts;
        private readonly int _rpInterval;
        private readonly bool _rpExp;
        private readonly int _rpFactor;
        private readonly int _rpMaxDelay;
        private readonly object? _customDeadLetterObj;
        private readonly object? _autoGenObj;
        private readonly long _serverConsumerTimeoutMs;
        private readonly SemaphoreSlim _channelGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _recoveryGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _prefetchSemaphore;

        private IConnection? _connection;
        private IChannel? _channel;
        private string _deadLetterQueueName = string.Empty;
        private volatile bool _stopping;
        private CancellationToken _lifetimeCt;

        public ConsumerInstance(
            IServiceProvider root,
            ILogger logger,
            ConnectionFactory connectionFactory,
            JsonSerializerOptions serializerOptions,
            IConsumerSettingsMarker marker)
        {
            _root = root;
            _logger = logger;
            _connectionFactory = connectionFactory;
            _serializerOptions = serializerOptions;
            _marker = marker;

            var settings = marker.SettingsInstance;

            _markerFactory = marker.Factory;
            
            _consumerType = marker.ConsumerType;
            
            _eventType = marker.EventType;
            
            _queueName = settings.QueueName;
            
            _consumerId = settings.ConsumerId ?? settings.QueueName;
            
            _timeout = settings.Timeout;
            
            _prefetch = settings.PrefetchCount;
            
            _autoGenerate = settings.AutoGenerate;

            _autoAckOnError = settings.AutoAckOnError;

            _unwrapEnvelopes = settings.UnwrapDeadLetterEnvelopes;

            var reprocessObj = root.GetService(typeof(DeadLetterReprocessSettings<>).MakeGenericType(_consumerType));

            var reprocessConfigured = reprocessObj != null
                && (bool)reprocessObj.GetType().GetProperty(nameof(DeadLetterReprocessSettings<object>.Enabled))!.GetValue(reprocessObj)!;

            var effectiveExtend = settings.ExtendDeadletterMessage;

            if (reprocessConfigured && !_autoGenerate)
            {
                logger.LogWarning("[RABBIT-FLOW]: Dead-letter reprocessor configured for {Consumer} but AutoGenerate is disabled. Reprocessor will be ignored.", _consumerType.Name);
            }
            else if (reprocessConfigured && _autoGenerate && !effectiveExtend)
            {
                logger.LogWarning("[RABBIT-FLOW]: Dead-letter reprocessor enabled for {Consumer}; forcing ExtendDeadletterMessage = true.", _consumerType.Name);
                effectiveExtend = true;
            }

            _extendDeadLetter = effectiveExtend;

            var retryPolicyObj = root.GetService(typeof(RetryPolicy<>).MakeGenericType(_consumerType))
                                ?? Activator.CreateInstance(typeof(RetryPolicy<>).MakeGenericType(_consumerType));

            var rpType = retryPolicyObj.GetType();

            _rpMax = (int)rpType.GetProperty(nameof(RetryPolicy<object>.MaxRetryCount))!.GetValue(retryPolicyObj)!;

            if (_rpMax < 0)
            {
                _rpMax = 0;
            }

            _totalAttempts = _rpMax + 1;
            
            _rpInterval = (int)rpType.GetProperty(nameof(RetryPolicy<object>.RetryInterval))!.GetValue(retryPolicyObj)!;
            
            _rpExp = (bool)rpType.GetProperty(nameof(RetryPolicy<object>.ExponentialBackoff))!.GetValue(retryPolicyObj)!;
            
            _rpFactor = (int)rpType.GetProperty(nameof(RetryPolicy<object>.ExponentialBackoffFactor))!.GetValue(retryPolicyObj)!;
            
            _rpMaxDelay = (int)rpType.GetProperty(nameof(RetryPolicy<object>.MaxRetryDelay))!.GetValue(retryPolicyObj)!;

            _customDeadLetterObj = root.GetService(typeof(CustomDeadLetterSettings<>).MakeGenericType(_consumerType));

            _autoGenObj = _autoGenerate
                ? (root.GetService(typeof(AutoGenerateSettings<>).MakeGenericType(_consumerType))
                    ?? Activator.CreateInstance(typeof(AutoGenerateSettings<>).MakeGenericType(_consumerType)))
                : null;

            _prefetchSemaphore = new SemaphoreSlim(_prefetch);

            long sumRetryDelaysMs = 0;

            for (int attemptIndex = 1; attemptIndex <= _rpMax; attemptIndex++)
            {
                long d = _rpInterval;

                if (_rpExp && attemptIndex > 1)
                {
                    try
                    {
                        var computed = checked((long)(_rpInterval * Math.Pow(_rpFactor, attemptIndex - 1)));
                        
                        d = Math.Min(computed, _rpMaxDelay);
                    }
                    catch 
                    { 
                        d = _rpMaxDelay; 
                    }
                }

                sumRetryDelaysMs += d;
            }

            const long serverTimeoutGraceMs = 30_000;

            _serverConsumerTimeoutMs = (long)(_timeout.TotalMilliseconds * _totalAttempts) + sumRetryDelaysMs + serverTimeoutGraceMs;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _lifetimeCt = cancellationToken;

            await EnsureConnectionAsync(cancellationToken);

            await EnsureChannelAsync(cancellationToken);
        }

        public async Task StopAsync()
        {
            _stopping = true;

            await _recoveryGate.WaitAsync();
            
            try
            {
                DisposeChannel();
                DisposeConnection();
            }
            finally
            {
                _recoveryGate.Release();
            }

            _channelGate.Dispose();
            _prefetchSemaphore.Dispose();
            _recoveryGate.Dispose();
        }

        private async Task EnsureConnectionAsync(CancellationToken ct)
        {
            if (_connection != null && _connection.IsOpen)
            {
                return;
            }

            DisposeConnection();

            _connection = await _connectionFactory.CreateConnectionAsync($"consumer_{_consumerId}", ct);

            _connection.ConnectionShutdownAsync += OnConnectionShutdownAsync;
            
            _connection.CallbackExceptionAsync += OnConnectionCallbackExceptionAsync;
            
            _connection.RecoverySucceededAsync += OnRecoverySucceededAsync;
            
            _connection.ConnectionRecoveryErrorAsync += OnConnectionRecoveryErrorAsync;
        }

        private async Task EnsureChannelAsync(CancellationToken ct)
        {
            if (_channel != null && _channel.IsOpen)
            {
                return;
            }

            DisposeChannel();

            _channel = await _connection!.CreateChannelAsync(cancellationToken: ct);

            _channel.CallbackExceptionAsync += OnChannelCallbackExceptionAsync;
            
            _channel.ChannelShutdownAsync += OnChannelShutdownAsync;

            await _channel.BasicQosAsync(0, _prefetch, false, ct);

            if (_autoGenerate && _autoGenObj != null)
            {
                await DeclareTopologyAsync(_channel, ct);
            }

            var consumer = new AsyncEventingBasicConsumer(_channel);

            consumer.ReceivedAsync += async (_, ea) =>
            {
                if (_stopping || _lifetimeCt.IsCancellationRequested)
                {
                    return;
                }
                try
                {
                    await _prefetchSemaphore.WaitAsync(_lifetimeCt);
                }
                catch { return; }

                _ = ProcessMessageAsync(ea);
            };

            await _channel.BasicConsumeAsync(queue: _queueName, autoAck: false, consumer: consumer, cancellationToken: ct);
        }

        private async Task DeclareTopologyAsync(IChannel channel, CancellationToken ct)
        {
            var autoGenObj = _autoGenObj!;

            var exchangeName = (string?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.ExchangeName))?.GetValue(autoGenObj) ?? $"{_queueName}-exchange";

            var routingKey = (string?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.RoutingKey))?.GetValue(autoGenObj) ?? $"{_queueName}-routing-key";

            var generateDeadletter = (bool?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.GenerateDeadletterQueue))?.GetValue(autoGenObj) ?? false;

            var durableQueue = (bool?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.DurableQueue))?.GetValue(autoGenObj) ?? true;

            var durableExchange = (bool?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.DurableExchange))?.GetValue(autoGenObj) ?? true;

            var autoDeleteQueue = (bool?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.AutoDeleteQueue))?.GetValue(autoGenObj) ?? false;

            var exclusiveQueue = (bool?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.ExclusiveQueue))?.GetValue(autoGenObj) ?? false;

            var exchangeType = autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.ExchangeType))?.GetValue(autoGenObj)?.ToString()?.ToLowerInvariant() ?? "direct";

            var generateExchange = (bool?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.GenerateExchange))?.GetValue(autoGenObj) ?? true;

            IDictionary<string, object?>? args = autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.Args))?.GetValue(autoGenObj) is IDictionary<string, object?> argsVal ? new Dictionary<string, object?>(argsVal) : null;

            if (generateDeadletter)
            {
                _deadLetterQueueName = $"{_queueName}-deadletter";
                
                var deadLetterExchange = $"{_queueName}-deadletter-exchange";
                
                var deadLetterRoutingKey = $"{_queueName}-deadletter-routing-key";

                await channel.QueueDeclareAsync(_deadLetterQueueName, durableQueue, false, autoDeleteQueue, null, cancellationToken: ct);
                
                await channel.ExchangeDeclareAsync(deadLetterExchange, "direct", durableExchange, cancellationToken: ct);
                
                await channel.QueueBindAsync(_deadLetterQueueName, deadLetterExchange, deadLetterRoutingKey);

                args ??= new Dictionary<string, object?>();
                
                args["x-dead-letter-exchange"] = deadLetterExchange;
                
                args["x-dead-letter-routing-key"] = deadLetterRoutingKey;
            }

            args ??= new Dictionary<string, object?>();

            if (!args.ContainsKey("x-consumer-timeout"))
            {
                args["x-consumer-timeout"] = _serverConsumerTimeoutMs;
            }

            await channel.QueueDeclareAsync(_queueName, durableQueue, exclusiveQueue, autoDeleteQueue, args);

            if (generateExchange)
            {
                await channel.ExchangeDeclareAsync(exchangeName, exchangeType, durableExchange);
                
                await channel.QueueBindAsync(_queueName, exchangeName, routingKey);
            }
        }

        private async Task ProcessMessageAsync(BasicDeliverEventArgs args)
        {
            var channel = _channel;
            
            var rootCt = _lifetimeCt;

            try
            {
                if (channel == null || !channel.IsOpen)
                {
                    return;
                }

                var body = args.Body.ToArray();

                var reprocessAttempts = RabbitFlowHeaders.ReadReprocessAttempts(args.BasicProperties?.Headers);
                
                var msgId = args.BasicProperties?.MessageId;

                var corrId = args.BasicProperties?.CorrelationId;

                if (_unwrapEnvelopes
                    && LooksLikeDeadLetterEnvelope(body)
                    && TryUnwrapDeadLetterEnvelope(body, out var unwrappedBody, out var inboundEnvelope))
                {
                    body = unwrappedBody;

                    msgId ??= inboundEnvelope!.MessageId;

                    corrId ??= inboundEnvelope!.CorrelationId;

                    _logger.LogWarning(
                        "[RABBIT-FLOW]: Detected manually replayed DeadLetterEnvelope on queue {Queue} for {Consumer}; unwrapped inner payload. Prefer the DeadLetterReprocessor for replays.",
                        _queueName, _consumerType.Name);
                }

                object? evt;

                try
                {
                    evt = _markerFactory.Deserialize(body, _serializerOptions) ?? throw new Exception("Deserialization returned null");
                }
                catch (Exception ex)
                {
                    await HandleErrorAsync(channel, args.DeliveryTag, null, body, reprocessAttempts, msgId, corrId, ex, rootCt);
                    return;
                }

                int remainingAttempts = _totalAttempts;

                Exception? lastException = null;

                while (remainingAttempts > 0 && !rootCt.IsCancellationRequested)
                {
                    using var attemptCts = new CancellationTokenSource(_timeout);
                    
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(attemptCts.Token, rootCt);
                    
                    var attemptCt = linked.Token;

                    using var scope = _root.CreateScope();

                    try
                    {
                        var messageContext = new RabbitFlowMessageContext(
                            args.BasicProperties?.MessageId,
                            args.BasicProperties?.CorrelationId,
                            args.Exchange,
                            args.RoutingKey,
                            args.BasicProperties?.Headers,
                            args.DeliveryTag,
                            args.Redelivered,
                            reprocessAttempts);

                        var consumerInstance = scope.ServiceProvider.GetRequiredService(_consumerType);

                        await _markerFactory.InvokeHandleAsync(consumerInstance, evt, messageContext, attemptCt).ConfigureAwait(false);

                        await SafeAckAsync(channel, args.DeliveryTag, rootCt);

                        return;
                    }
                    catch (OperationCanceledException ex) when (attemptCts.IsCancellationRequested && !rootCt.IsCancellationRequested)
                    {
                        lastException = ex;
                        await ApplyRetryDelay(remainingAttempts, rootCt);
                    }
                    catch (RabbitFlowTransientException ex)
                    {
                        lastException = ex;
                        await ApplyRetryDelay(remainingAttempts, rootCt);
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
                    await HandleErrorAsync(channel, args.DeliveryTag, evt, body, reprocessAttempts, msgId, corrId, lastException ?? new Exception("Unknown error"), rootCt);

                    if (_customDeadLetterObj != null && evt != null)
                    {
                        try
                        {
                            var queueProp = _customDeadLetterObj.GetType().GetProperty("DeadletterQueueName");
                            var deadQueue = queueProp?.GetValue(_customDeadLetterObj) as string;

                            if (!string.IsNullOrWhiteSpace(deadQueue) && _markerFactory.PublishToCustomDeadletter != null)
                            {
                                await _markerFactory.PublishToCustomDeadletter(evt, deadQueue!, _root);
                            }
                        }
                        catch (Exception dlxEx)
                        {
                            _logger.LogError(dlxEx, "[RABBIT-FLOW]: Error publishing to custom dead-letter queue.");
                        }
                    }
                }
            }
            catch (AlreadyClosedException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RABBIT-FLOW]: Unhandled exception in message processing (HostedService).");
            }
            finally
            {
                try { _prefetchSemaphore.Release(); } catch { }
            }
        }

        private async Task ApplyRetryDelay(int remainingAttempts, CancellationToken ct)
        {
            if (remainingAttempts <= 1)
            {
                return;
            }

            int attemptIndex = _totalAttempts - remainingAttempts + 1;
            long delay = _rpInterval;

            if (_rpExp && attemptIndex > 1)
            {
                try
                {
                    var computed = checked(_rpInterval * (long)Math.Pow(_rpFactor, attemptIndex - 1));
                    delay = Math.Min(computed, _rpMaxDelay);
                }
                catch { delay = _rpMaxDelay; }
            }

            try { await Task.Delay((int)delay, ct); } catch { }
        }

        private async Task SafeAckAsync(IChannel channel, ulong deliveryTag, CancellationToken ct)
        {
            await _channelGate.WaitAsync(ct).ConfigureAwait(false);
            try { await channel.BasicAckAsync(deliveryTag, false, ct); } catch { }
            finally { _channelGate.Release(); }
        }

        private async Task HandleErrorAsync(IChannel channel, ulong deliveryTag, object? evt, byte[]? body, int reprocessAttempts, string? messageId, string? correlationId, Exception exception, CancellationToken ct)
        {
            await _channelGate.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                if (_autoAckOnError)
                {
                    try 
                    { 
                        await channel.BasicAckAsync(deliveryTag, false, ct); 
                    } 
                    catch { }
                    return;
                }

                if (_extendDeadLetter && !string.IsNullOrEmpty(_deadLetterQueueName))
                {
                    var envelope = new DeadLetterEnvelope
                    {
                        DateUtc = DateTime.UtcNow,
                        MessageType = _eventType.Name,
                        MessageId = messageId,
                        CorrelationId = correlationId,
                        MessageData = TryParseJsonElement(body),
                        ExceptionType = exception?.GetType().Name,
                        ErrorMessage = exception?.Message,
                        StackTrace = exception?.StackTrace,
                        Source = exception?.Source,
                        ReprocessAttempts = reprocessAttempts
                    };

                    const int MaxDepth = 10;
                    
                    var temp = exception;
                    
                    var depth = 0;

                    while (temp?.InnerException != null && depth < MaxDepth)
                    {
                        temp = temp.InnerException;

                        if (temp != null)
                        {
                            envelope.InnerExceptions.Add(new DeadLetterInnerException
                            {
                                ExceptionType = temp.GetType().Name,
                                ErrorMessage = temp.Message,
                                StackTrace = temp.StackTrace,
                                Source = temp.Source
                            });
                        }
                        depth++;
                    }

                    try
                    {
                        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, _serializerOptions));
                        
                        await channel.BasicPublishAsync(exchange: "", routingKey: _deadLetterQueueName, body: bytes, cancellationToken: ct);
                        
                        await channel.BasicAckAsync(deliveryTag, false, ct);
                    }
                    catch { }
                }
                else
                {
                    try 
                    { 
                        await channel.BasicNackAsync(deliveryTag, false, false, ct);

                    } catch { }
                }
            }
            finally { _channelGate.Release(); }
        }

        private Task OnChannelCallbackExceptionAsync(object sender, CallbackExceptionEventArgs args)
        {
            _logger.LogWarning(args.Exception, "[RABBIT-FLOW]: Channel callback exception for {Consumer}.", _consumerType.Name);

            return Task.CompletedTask;
        }

        private Task OnChannelShutdownAsync(object sender, ShutdownEventArgs args)
        {
            _logger.LogWarning("[RABBIT-FLOW]: Channel shutdown for {Consumer}. ReplyCode: {Code}, ReplyText: {Text}", _consumerType.Name, args.ReplyCode, args.ReplyText);

            if (_stopping || args.Initiator == ShutdownInitiator.Application)
            {
                return Task.CompletedTask;
            }

            _ = TriggerRecoveryAsync();

            return Task.CompletedTask;
        }

        private Task OnConnectionShutdownAsync(object sender, ShutdownEventArgs args)
        {
            _logger.LogWarning("[RABBIT-FLOW]: Connection shutdown for {Consumer}. ReplyCode: {Code}, ReplyText: {Text}", _consumerType.Name, args.ReplyCode, args.ReplyText);

            if (_stopping || args.Initiator == ShutdownInitiator.Application)
            {
                return Task.CompletedTask;
            }

            _ = TriggerRecoveryAsync();

            return Task.CompletedTask;
        }

        private Task OnConnectionCallbackExceptionAsync(object sender, CallbackExceptionEventArgs args)
        {
            _logger.LogError(args.Exception, "[RABBIT-FLOW]: Connection callback exception for {Consumer}.", _consumerType.Name);
            return Task.CompletedTask;
        }

        private Task OnRecoverySucceededAsync(object sender, AsyncEventArgs args)
        {
            _logger.LogInformation("[RABBIT-FLOW]: Connection recovery succeeded for {Consumer}.", _consumerType.Name);
            return Task.CompletedTask;
        }

        private Task OnConnectionRecoveryErrorAsync(object sender, ConnectionRecoveryErrorEventArgs args)
        {
            _logger.LogError(args.Exception, "[RABBIT-FLOW]: Connection recovery error for {Consumer}.", _consumerType.Name);
            return Task.CompletedTask;
        }

        private async Task TriggerRecoveryAsync()
        {
            if (_stopping)
            {
                return;
            }

            if (!await _recoveryGate.WaitAsync(0))
            {
                return;
            }

            try
            {
                int attempt = 0;

                while (!_stopping)
                {
                    attempt++;

                    try
                    {
                        await EnsureConnectionAsync(_lifetimeCt);

                        await EnsureChannelAsync(_lifetimeCt);

                        _logger.LogInformation("[RABBIT-FLOW]: Consumer {Consumer} recovered after {Attempts} attempt(s).", _consumerType.Name, attempt);
                        return;
                    }
                    catch (OperationCanceledException) when (_lifetimeCt.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        var delay = Math.Min(1000 * (int)Math.Pow(2, Math.Min(attempt, 6)), 30_000);
                        _logger.LogError(ex, "[RABBIT-FLOW]: Recovery attempt {Attempt} failed for {Consumer}. Retrying in {Delay}ms.", attempt, _consumerType.Name, delay);

                        try { await Task.Delay(delay, _lifetimeCt); } catch { return; }
                    }
                }
            }
            finally
            {
                _recoveryGate.Release();
            }
        }

        private void DisposeChannel()
        {
            var ch = _channel;

            _channel = null;

            if (ch == null)
            {
                return;
            }

            try
            {
                ch.CallbackExceptionAsync -= OnChannelCallbackExceptionAsync;

                ch.ChannelShutdownAsync -= OnChannelShutdownAsync;
            }
            catch { }

            try { ch.Dispose(); } catch { }
        }

        private void DisposeConnection()
        {
            var conn = _connection;

            _connection = null;

            if (conn == null)
            {
                return;
            }

            try
            {
                conn.ConnectionShutdownAsync -= OnConnectionShutdownAsync;
                
                conn.CallbackExceptionAsync -= OnConnectionCallbackExceptionAsync;
                
                conn.RecoverySucceededAsync -= OnRecoverySucceededAsync;
                
                conn.ConnectionRecoveryErrorAsync -= OnConnectionRecoveryErrorAsync;
            }
            catch { }

            try 
            { 
                conn.Dispose(); 
            } 
            catch { }
        }

        private static JsonElement? TryParseJsonElement(byte[]? body)
        {
            if (body == null || body.Length == 0)
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(body);

                return doc.RootElement.Clone();
            }
            catch
            {
                return null;
            }
        }

        private static readonly byte[] EnvelopeKey_MessageData = Encoding.UTF8.GetBytes("\"messageData\"");
       
        private static readonly byte[] EnvelopeKey_ExceptionType = Encoding.UTF8.GetBytes("\"exceptionType\"");

        // Cheap byte-level fingerprint to decide whether a body is worth parsing as a DeadLetterEnvelope.
        
        // Scans for the two most distinctive envelope keys without touching JsonSerializer.
        private static bool LooksLikeDeadLetterEnvelope(byte[]? body)
        {
            if (body == null || body.Length < 32)
            {
                return false;
            }

            int i = 0;

            while (i < body.Length && (body[i] == (byte)' ' || body[i] == (byte)'\t' || body[i] == (byte)'\r' || body[i] == (byte)'\n'))
            {
                i++;
            }

            if (i >= body.Length || body[i] != (byte)'{')
            {
                return false;
            }

            var span = new ReadOnlySpan<byte>(body);

            return span.IndexOf(EnvelopeKey_MessageData.AsSpan()) >= 0
                && span.IndexOf(EnvelopeKey_ExceptionType.AsSpan()) >= 0;
        }

        private bool TryUnwrapDeadLetterEnvelope(byte[]? body, out byte[] innerBody, out DeadLetterEnvelope? envelope)
        {
            innerBody = body ?? Array.Empty<byte>();

            envelope = null;

            if (body == null || body.Length == 0)
            {
                return false;
            }

            try
            {
                envelope = JsonSerializer.Deserialize<DeadLetterEnvelope>(body, _serializerOptions);
            }
            catch
            {
                envelope = null;
                return false;
            }

            if (envelope == null || envelope.MessageData == null || string.IsNullOrEmpty(envelope.ExceptionType))
            {
                return false;
            }

            try
            {
                innerBody = Encoding.UTF8.GetBytes(envelope.MessageData.Value.GetRawText());
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}