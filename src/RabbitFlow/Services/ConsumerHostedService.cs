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
using System.Diagnostics;
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

        private int _stopped;

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
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }

            foreach (var instance in _instances)
            {
                try
                {
                    await instance.StopAsync(cancellationToken);
                }
                catch (ObjectDisposedException)
                {
                }
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
        private readonly object? _autoGenObj;
        private readonly long _serverConsumerTimeoutMs;
        private readonly SemaphoreSlim _channelGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _recoveryGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _prefetchSemaphore;

        private IConnection? _connection;
        private IChannel? _channel;
        private string _deadLetterQueueName = string.Empty;
        private string _deadLetterExchangeName = string.Empty;
        private string _deadLetterRoutingKey = string.Empty;
        private volatile bool _stopping;
        private int _stopped;
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
                                ?? Activator.CreateInstance(typeof(RetryPolicy<>).MakeGenericType(_consumerType))!;

            var rpType = retryPolicyObj.GetType();

            _rpMax = (int)rpType.GetProperty(nameof(RetryPolicy<object>.MaxRetryCount))!.GetValue(retryPolicyObj)!;

            if (_rpMax < 0)
            {
                _rpMax = 0;
            }

            _totalAttempts = _rpMax + 1;
            
            _rpInterval = (int)rpType.GetProperty(nameof(RetryPolicy<object>.RetryInterval))!.GetValue(retryPolicyObj)!;

            _autoGenObj = _autoGenerate
                ? (root.GetService(typeof(AutoGenerateSettings<>).MakeGenericType(_consumerType))
                    ?? Activator.CreateInstance(typeof(AutoGenerateSettings<>).MakeGenericType(_consumerType)))
                : null;

            _prefetchSemaphore = new SemaphoreSlim(_prefetch);

            // Fixed interval between every retry: _rpMax retries means _rpMax delays of _rpInterval each.
            long sumRetryDelaysMs = (long)_rpInterval * _rpMax;

            const long serverTimeoutGraceMs = 30_000;

            // RabbitMQ rejects consumer timeouts below one minute, so floor the computed value at 60s.
            // (A short Timeout with no retries can otherwise land under the minimum.)
            const long minConsumerTimeoutMs = 60_000;

            var computedConsumerTimeoutMs = (long)(_timeout.TotalMilliseconds * _totalAttempts) + sumRetryDelaysMs + serverTimeoutGraceMs;

            _serverConsumerTimeoutMs = Math.Max(minConsumerTimeoutMs, computedConsumerTimeoutMs);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _lifetimeCt = cancellationToken;

            await EnsureConnectionAsync(cancellationToken);

            await EnsureChannelAsync(cancellationToken);
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }

            _stopping = true;

            // Bounded drain: wait for in-flight handlers (the prefetch semaphore back at full capacity)
            // before closing the channel, so finished work is acked and failures are dead-lettered
            // instead of being silently redelivered after restart. The host's shutdown token (or the
            // 30s cap) bounds the wait when a handler ignores cancellation.
            var drainDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

            try
            {
                while (_prefetchSemaphore.CurrentCount < _prefetch
                    && DateTime.UtcNow < drainDeadline
                    && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                await _recoveryGate.WaitAsync().ConfigureAwait(false);

                try
                {
                    DisposeChannel();
                    DisposeConnection();
                }
                finally
                {
                    try { _recoveryGate.Release(); } catch (ObjectDisposedException) { }
                }
            }
            catch (ObjectDisposedException)
            {
            }

            try { _channelGate.Dispose(); } catch (ObjectDisposedException) { }
            try { _prefetchSemaphore.Dispose(); } catch (ObjectDisposedException) { }
            try { _recoveryGate.Dispose(); } catch (ObjectDisposedException) { }
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

                if (_stopping || _lifetimeCt.IsCancellationRequested)
                {
                    return;
                }

                _ = ProcessMessageAsync(ea);
            };

            await _channel.BasicConsumeAsync(queue: _queueName, autoAck: false, consumer: consumer, cancellationToken: ct);
        }

        private async Task DeclareTopologyAsync(IChannel channel, CancellationToken ct)
        {
            var autoGenObj = _autoGenObj!;

            var exchangeName = (string?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.ExchangeName))?.GetValue(autoGenObj) ?? RabbitFlowTopologyNames.Exchange(_queueName);

            var routingKey = (string?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.RoutingKey))?.GetValue(autoGenObj) ?? RabbitFlowTopologyNames.RoutingKey(_queueName);

            var generateDeadletter = (bool?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.GenerateDeadletterQueue))?.GetValue(autoGenObj) ?? false;

            var durableQueue = (bool?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.DurableQueue))?.GetValue(autoGenObj) ?? true;

            var durableExchange = (bool?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.DurableExchange))?.GetValue(autoGenObj) ?? true;

            var autoDeleteQueue = (bool?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.AutoDeleteQueue))?.GetValue(autoGenObj) ?? false;

            var exclusiveQueue = (bool?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.ExclusiveQueue))?.GetValue(autoGenObj) ?? false;

            var exchangeType = autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.ExchangeType))?.GetValue(autoGenObj)?.ToString()?.ToLowerInvariant() ?? "direct";

            var generateExchange = (bool?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.GenerateExchange))?.GetValue(autoGenObj) ?? true;

            IDictionary<string, object?>? args = autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.Args))?.GetValue(autoGenObj) is IDictionary<string, object?> argsVal ? new Dictionary<string, object?>(argsVal) : null;

            var maxPriority = (byte?)autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.MaxPriority))?.GetValue(autoGenObj);

            if (maxPriority.HasValue)
            {
                args ??= new Dictionary<string, object?>();

                if (!args.ContainsKey("x-max-priority"))
                {
                    args["x-max-priority"] = (int)maxPriority.Value;
                }
            }

            var replicas = autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.DeadLetterReplicas))?.GetValue(autoGenObj) as System.Collections.IEnumerable;

            if (generateDeadletter)
            {
                _deadLetterQueueName = RabbitFlowTopologyNames.DeadLetterQueue(_queueName);

                _deadLetterExchangeName = RabbitFlowTopologyNames.DeadLetterExchange(_queueName);

                _deadLetterRoutingKey = RabbitFlowTopologyNames.DeadLetterRoutingKey(_queueName);

                await channel.QueueDeclareAsync(_deadLetterQueueName, durableQueue, false, autoDeleteQueue, null, cancellationToken: ct);

                await channel.ExchangeDeclareAsync(_deadLetterExchangeName, "direct", durableExchange, cancellationToken: ct);

                await channel.QueueBindAsync(_deadLetterQueueName, _deadLetterExchangeName, _deadLetterRoutingKey);

                args ??= new Dictionary<string, object?>();

                args["x-dead-letter-exchange"] = _deadLetterExchangeName;

                args["x-dead-letter-routing-key"] = _deadLetterRoutingKey;

                if (replicas != null)
                {
                    foreach (var replica in replicas)
                    {
                        if (replica == null)
                        {
                            continue;
                        }

                        var replicaType = replica.GetType();

                        var replicaQueue = (string?)replicaType.GetProperty(nameof(DeadLetterReplica.QueueName))?.GetValue(replica);

                        if (string.IsNullOrWhiteSpace(replicaQueue))
                        {
                            _logger.LogWarning("[RABBIT-FLOW]: Skipping dead-letter replica with empty QueueName for {Consumer}.", _consumerType.Name);
                            continue;
                        }

                        var replicaDurable = (bool?)replicaType.GetProperty(nameof(DeadLetterReplica.Durable))?.GetValue(replica) ?? true;

                        var replicaAutoDelete = (bool?)replicaType.GetProperty(nameof(DeadLetterReplica.AutoDelete))?.GetValue(replica) ?? false;

                        var replicaArgs = replicaType.GetProperty(nameof(DeadLetterReplica.Arguments))?.GetValue(replica) as IDictionary<string, object?>;

                        await channel.QueueDeclareAsync(replicaQueue!, replicaDurable, false, replicaAutoDelete, replicaArgs, cancellationToken: ct);

                        await channel.QueueBindAsync(replicaQueue!, _deadLetterExchangeName, _deadLetterRoutingKey);
                    }
                }
            }
            else if (replicas != null)
            {
                var replicaCount = 0;

                foreach (var _ in replicas)
                {
                    replicaCount++;
                }

                if (replicaCount > 0)
                {
                    _logger.LogWarning("[RABBIT-FLOW]: {Consumer} has {Count} DeadLetterReplicas configured but GenerateDeadletterQueue is false. Replicas ignored.", _consumerType.Name, replicaCount);
                }
            }

            args ??= new Dictionary<string, object?>();

            if (!args.ContainsKey("x-consumer-timeout"))
            {
                args["x-consumer-timeout"] = _serverConsumerTimeoutMs;
            }

            try
            {
                await channel.QueueDeclareAsync(_queueName, durableQueue, exclusiveQueue, autoDeleteQueue, args);
            }
            catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 406)
            {
                // PRECONDITION_FAILED: the queue already exists with arguments that differ from what we're
                // declaring. The most common cause is a changed Timeout/MaxRetryCount/RetryInterval altering the
                // derived x-consumer-timeout. Surface a clear, actionable error instead of letting this surface
                // as a cryptic channel shutdown and silent recovery loop. The channel is dead after a 406, so rethrow.
                _logger.LogError(ex,
                    "[RABBIT-FLOW]: Queue '{Queue}' already exists with arguments that differ from the auto-generated topology " +
                    "(commonly a changed Timeout/MaxRetryCount/RetryInterval, which alters the derived x-consumer-timeout). " +
                    "RabbitMQ rejected the redeclaration with PRECONDITION_FAILED. Delete or migrate the queue, or revert the " +
                    "changed settings, before the consumer for {Consumer} can start.",
                    _queueName, _consumerType.Name);

                throw;
            }

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

                using var activity = RabbitFlowDiagnostics.StartProcess(_queueName, args.BasicProperties?.Headers, msgId, corrId, args.Redelivered);

                var startTimestamp = Stopwatch.GetTimestamp();

                void RecordConsumeMetrics(string outcome)
                {
                    var elapsedSeconds = (Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency;
                    var queueTag = new KeyValuePair<string, object?>("queue", _queueName);
                    var outcomeTag = new KeyValuePair<string, object?>("outcome", outcome);

                    RabbitFlowDiagnostics.ConsumedMessages.Add(1, queueTag, outcomeTag);
                    RabbitFlowDiagnostics.ConsumeDuration.Record(elapsedSeconds, queueTag, outcomeTag);
                }

                object? evt;

                try
                {
                    evt = _markerFactory.Deserialize(body, _serializerOptions) ?? throw new Exception("Deserialization returned null");
                }
                catch (Exception ex)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    RecordConsumeMetrics("failure");
                    await HandleErrorAsync(channel, args.DeliveryTag, null, body, reprocessAttempts, msgId, corrId, args.BasicProperties, ex, rootCt);
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
                            reprocessAttempts,
                            ReadDeliveryMode(args.BasicProperties),
                            ReadType(args.BasicProperties),
                            ReadAppId(args.BasicProperties),
                            ReadExpiration(args.BasicProperties),
                            ReadPriority(args.BasicProperties),
                            ReadTimestamp(args.BasicProperties),
                            ReadReplyTo(args.BasicProperties),
                            ReadContentType(args.BasicProperties));

                        var consumerInstance = scope.ServiceProvider.GetRequiredService(_consumerType);

                        await _markerFactory.InvokeHandleAsync(consumerInstance, evt, messageContext, attemptCt).ConfigureAwait(false);

                        await SafeAckAsync(channel, args.DeliveryTag, rootCt);

                        RecordConsumeMetrics("success");

                        return;
                    }
                    catch (OperationCanceledException ex) when (attemptCts.IsCancellationRequested && !rootCt.IsCancellationRequested)
                    {
                        lastException = ex;

                        if (remainingAttempts > 1)
                        {
                            RabbitFlowDiagnostics.RetriedMessages.Add(1, new KeyValuePair<string, object?>("queue", _queueName));
                        }

                        await ApplyRetryDelay(remainingAttempts, rootCt);
                    }
                    catch (Exception ex) when (TransientExceptionClassifier.IsTransient(ex))
                    {
                        lastException = ex;

                        if (remainingAttempts > 1)
                        {
                            RabbitFlowDiagnostics.RetriedMessages.Add(1, new KeyValuePair<string, object?>("queue", _queueName));
                        }

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
                    activity?.SetStatus(ActivityStatusCode.Error, lastException?.Message ?? "Unknown error");
                    RecordConsumeMetrics("failure");
                    await HandleErrorAsync(channel, args.DeliveryTag, evt, body, reprocessAttempts, msgId, corrId, args.BasicProperties, lastException ?? new Exception("Unknown error"), rootCt);
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

            try
            {
                await Task.Delay(_rpInterval, ct);
            }
            catch { }
        }

        private async Task SafeAckAsync(IChannel channel, ulong deliveryTag, CancellationToken ct)
        {
            await _channelGate.WaitAsync(ct).ConfigureAwait(false);
            
            try 
            { 
                await channel.BasicAckAsync(deliveryTag, false, ct); 
            } 
            catch { }
            finally 
            { 
                _channelGate.Release(); 
            }
        }

        private async Task HandleErrorAsync(IChannel channel, ulong deliveryTag, object? evt, byte[]? body, int reprocessAttempts, string? messageId, string? correlationId, IReadOnlyBasicProperties? originalProperties, Exception exception, CancellationToken ct)
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
                        ReprocessAttempts = reprocessAttempts,
                        IsTransient = TransientExceptionClassifier.IsTransient(exception),
                        Properties = CaptureMessageProperties(originalProperties)
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

                    var queueTag = new KeyValuePair<string, object?>("queue", _queueName);

                    byte[]? bytes = null;

                    try
                    {
                        bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, _serializerOptions));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[RABBIT-FLOW]: Failed to serialize dead-letter envelope for {Consumer}; falling back to nack-driven dead-lettering.", _consumerType.Name);
                    }

                    var published = false;

                    if (bytes != null)
                    {
                        try
                        {
                            await channel.BasicPublishAsync(exchange: _deadLetterExchangeName, routingKey: _deadLetterRoutingKey, body: bytes, cancellationToken: ct);

                            published = true;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[RABBIT-FLOW]: Failed to publish dead-letter envelope to {Exchange} for {Consumer}; falling back to nack-driven dead-lettering.", _deadLetterExchangeName, _consumerType.Name);
                        }
                    }

                    if (published)
                    {
                        // Envelope is safely in the DLQ. Ack the original; if the ack fails the message will
                        // be redelivered (at-least-once) and may yield a duplicate envelope on a later pass.
                        try
                        {
                            await channel.BasicAckAsync(deliveryTag, false, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[RABBIT-FLOW]: Dead-letter envelope published for {Consumer} but the ack of the original delivery failed; it may be redelivered.", _consumerType.Name);
                        }

                        RabbitFlowDiagnostics.DeadLetteredMessages.Add(1, queueTag);
                    }
                    else
                    {
                        // The envelope could not be produced or published. Don't leave the original delivery
                        // unacked: a persistent failure would have it redelivered, reprocessed, and re-failed
                        // forever. Fall back to broker-native dead-lettering (the queue's x-dead-letter-exchange)
                        // so the raw message still leaves the main queue and the redelivery loop is broken.
                        try
                        {
                            await channel.BasicNackAsync(deliveryTag, false, false, ct);

                            RabbitFlowDiagnostics.DeadLetteredMessages.Add(1, queueTag);
                        }
                        catch { }
                    }
                }
                else
                {
                    try
                    {
                        await channel.BasicNackAsync(deliveryTag, false, false, ct);

                        RabbitFlowDiagnostics.DeadLetteredMessages.Add(1, new KeyValuePair<string, object?>("queue", _queueName));

                    } catch { }
                }
            }
            finally { _channelGate.Release(); }
        }

        private static DeadLetterMessageProperties? CaptureMessageProperties(IReadOnlyBasicProperties? properties)
        {
            if (properties == null)
            {
                return null;
            }

            Dictionary<string, string?>? headers = null;

            if (properties.Headers != null)
            {
                foreach (var kvp in properties.Headers)
                {
                    if (kvp.Key == RabbitFlowHeaders.ReprocessAttempts)
                    {
                        continue; // tracked by the envelope's ReprocessAttempts counter
                    }

                    headers ??= new Dictionary<string, string?>();

                    headers[kvp.Key] = kvp.Value switch
                    {
                        null => null,
                        byte[] bytes => Encoding.UTF8.GetString(bytes),
                        string text => text,
                        _ => kvp.Value.ToString()
                    };
                }
            }

            return new DeadLetterMessageProperties
            {
                DeliveryMode = ReadDeliveryMode(properties),
                Type = ReadType(properties),
                AppId = ReadAppId(properties),
                Priority = ReadPriority(properties),
                ContentType = ReadContentType(properties),
                ReplyTo = ReadReplyTo(properties),
                Headers = headers
            };
        }

        private static MessageDeliveryMode? ReadDeliveryMode(IReadOnlyBasicProperties? properties)
        {
            if (properties == null || !properties.IsDeliveryModePresent())
            {
                return null;
            }

            return (MessageDeliveryMode)(byte)properties.DeliveryMode;
        }

        private static string? ReadType(IReadOnlyBasicProperties? properties)
            => properties != null && properties.IsTypePresent() ? properties.Type : null;

        private static string? ReadAppId(IReadOnlyBasicProperties? properties)
            => properties != null && properties.IsAppIdPresent() ? properties.AppId : null;

        private static TimeSpan? ReadExpiration(IReadOnlyBasicProperties? properties)
        {
            if (properties == null || !properties.IsExpirationPresent())
            {
                return null;
            }

            if (long.TryParse(properties.Expiration, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ms))
            {
                return TimeSpan.FromMilliseconds(ms);
            }

            return null;
        }

        private static byte? ReadPriority(IReadOnlyBasicProperties? properties)
            => properties != null && properties.IsPriorityPresent() ? properties.Priority : (byte?)null;

        private static DateTimeOffset? ReadTimestamp(IReadOnlyBasicProperties? properties)
        {
            if (properties == null || !properties.IsTimestampPresent())
            {
                return null;
            }

            return DateTimeOffset.FromUnixTimeSeconds(properties.Timestamp.UnixTime);
        }

        private static string? ReadReplyTo(IReadOnlyBasicProperties? properties)
            => properties != null && properties.IsReplyToPresent() ? properties.ReplyTo : null;

        private static string? ReadContentType(IReadOnlyBasicProperties? properties)
            => properties != null && properties.IsContentTypePresent() ? properties.ContentType : null;

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
