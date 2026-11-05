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

            var hostSettings = _root.GetService<HostSettings>() ?? new HostSettings();

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

                var instance = new ConsumerInstance(_root, _logger, connectionFactory, serializerOptions, hostSettings, marker);

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
        private readonly int? _maxBrokerDeliveries;
        private readonly bool _recoveryEnabled;
        private readonly int _recoveryBaseDelayMs;
        private readonly object? _autoGenObj;
        private readonly long? _serverConsumerTimeoutMs;
        private readonly SemaphoreSlim _channelGate = new SemaphoreSlim(1, 1);
        private readonly ConsumerRecoveryGate _recoveryGate = new ConsumerRecoveryGate();
        private readonly SemaphoreSlim _prefetchSemaphore;

        // Cancelled by StopAsync. A delivery waiting for a prefetch slot does so on the client's consumer dispatcher;
        // closing the channel waits for that dispatcher to drain, so a waiter left behind (slot held by a handler
        // that ignores cancellation) would stall shutdown until the watchdog abandons that handler. Cancelling the
        // wait lets the delivery go unacked (the broker redelivers it after the channel closes) and shutdown proceed.
        private readonly CancellationTokenSource _stopCts = new CancellationTokenSource();

        // Publisher confirms for the consumer channel: the only thing this channel publishes is the dead-letter
        // envelope, and we must know it actually reached the broker before acking the original delivery. With
        // tracking enabled, BasicPublishAsync awaits the broker ack and throws if the message is not confirmed,
        // so a lost publish surfaces as an exception and falls back to nack-driven dead-lettering instead of
        // silently dropping the message. Consuming on a confirm-enabled channel is unaffected.
        private static readonly CreateChannelOptions ConfirmChannelOptions =
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);

        // Extra time a handler gets AFTER its attempt token is cancelled to unwind cooperatively before the
        // wait on it is abandoned. Without this, a handler stuck in a non-cancellable await holds its prefetch
        // slot forever: the queue keeps a live consumer that processes nothing.
        private static readonly TimeSpan HandlerAbandonGrace = TimeSpan.FromSeconds(30);

        // How long shutdown waits for a running recovery pass to leave the gate before closing without it. The pass
        // stops at its next iteration once _stopping is set and its waits observe the stop token, so this is normally
        // a few milliseconds; the cap keeps a stuck connection attempt from stretching the host's shutdown budget.
        private static readonly TimeSpan RecoveryGateStopWait = TimeSpan.FromSeconds(10);

        private IConnection? _connection;
        private IChannel? _channel;
        private AsyncEventingBasicConsumer? _consumer;
        private string? _consumerTag;
        private string _deadLetterQueueName = string.Empty;
        private string _deadLetterExchangeName = string.Empty;
        private string _deadLetterRoutingKey = string.Empty;
        // What the dead-letter queue, its exchange, binding and replicas were declared with, kept so they can be
        // declared again on the consumer channel when the broker returns an envelope as unroutable.
        private DeadLetterTopology? _deadLetterTopology;
        private volatile bool _stopping;
        private volatile bool _rebuilding;
        // Set once the broker rejects the 'x-consumer-timeout' queue argument (RabbitMQ 4.x), so later (re)declares
        // and recovery passes don't keep re-adding it and paying the extra rejected round trip.
        private volatile bool _consumerTimeoutArgRejected;
        private int _stopped;
        private CancellationToken _lifetimeCt;

        public ConsumerInstance(
            IServiceProvider root,
            ILogger logger,
            ConnectionFactory connectionFactory,
            JsonSerializerOptions serializerOptions,
            HostSettings hostSettings,
            IConsumerSettingsMarker marker)
        {
            _root = root;
            _logger = logger;
            _connectionFactory = connectionFactory;
            _serializerOptions = serializerOptions;
            _marker = marker;

            _recoveryEnabled = hostSettings.AutomaticRecoveryEnabled;

            // Base delay for the library's own recovery backoff; floored at 1s so a misconfigured tiny interval
            // never spins the recovery loop. See HostSettings.NetworkRecoveryInterval.
            _recoveryBaseDelayMs = (int)Math.Max(1000, hostSettings.NetworkRecoveryInterval.TotalMilliseconds);

            var settings = marker.SettingsInstance;

            _markerFactory = marker.Factory;
            
            _consumerType = marker.ConsumerType;
            
            _eventType = marker.EventType;
            
            _queueName = settings.QueueName;
            
            _consumerId = settings.ConsumerId ?? settings.QueueName;
            
            // Any non-positive Timeout means "no handler timeout". Only Timeout.InfiniteTimeSpan (-1 ms) is accepted by
            // CancellationTokenSource for that; TimeSpan.Zero would cancel every attempt at once and other negative
            // values would throw, so normalise here.
            _timeout = settings.Timeout <= TimeSpan.Zero ? Timeout.InfiniteTimeSpan : settings.Timeout;

            _prefetch = settings.PrefetchCount;
            
            _autoGenerate = settings.AutoGenerate;

            _autoAckOnError = settings.AutoAckOnError;

            _unwrapEnvelopes = settings.UnwrapDeadLetterEnvelopes;

            _maxBrokerDeliveries = settings.MaxBrokerDeliveries is int max && max > 0 ? max : (int?)null;

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

            _serverConsumerTimeoutMs = ConsumerTimeoutRules.ServerTimeoutMs(_timeout, _totalAttempts, _rpInterval);

            if (_autoGenerate && _serverConsumerTimeoutMs == null)
            {
                logger.LogInformation(
                    "[RABBIT-FLOW]: {Consumer} has no handler timeout (Timeout <= 0), so queue '{Queue}' is declared without 'x-consumer-timeout'. " +
                    "The broker's own consumer timeout (global setting or policy) still applies where the queue type evaluates it.",
                    _consumerType.Name, _queueName);
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _lifetimeCt = cancellationToken;

            // A host that aborts startup cancels this token; release any delivery waiting for a prefetch slot then too.
            _lifetimeCt.Register(() =>
            {
                try { _stopCts.Cancel(); } catch (ObjectDisposedException) { }
            });

            try
            {
                await EnsureConnectionAsync(cancellationToken);

                await EnsureChannelAsync(cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // A consumer that fails to set up (topology declare, bind, consume) must not bring the whole host
                // down at startup: one broken consumer should not leave the entire application unable to start.
                // Log it and, when recovery is enabled, keep retrying in the background; otherwise the consumer
                // stays down but the host comes up.
                _logger.LogError(ex,
                    "[RABBIT-FLOW]: Initial setup failed for {Consumer}. The host will continue to start. {Recovery}",
                    _consumerType.Name,
                    _recoveryEnabled
                        ? "Recovery is enabled; the consumer will keep retrying in the background."
                        : "Recovery is disabled (AutomaticRecoveryEnabled = false); the consumer stays down until restart.");

                if (_recoveryEnabled)
                {
                    _ = TriggerRecoveryAsync();
                }
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }

            _stopping = true;

            try { _stopCts.Cancel(); } catch (ObjectDisposedException) { }

            // Stop the broker from sending more deliveries while the in-flight ones drain: each ack would otherwise
            // free a prefetch credit and bring in a message that can only be left unacked and redelivered.
            var channelToCancel = _channel;

            var tagToCancel = _consumerTag;

            if (channelToCancel != null && channelToCancel.IsOpen && tagToCancel != null)
            {
                try
                {
                    await Task.WhenAny(channelToCancel.BasicCancelAsync(tagToCancel, cancellationToken: CancellationToken.None), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[RABBIT-FLOW]: Cancelling the consumer for {Consumer} on queue '{Queue}' at shutdown failed.", _consumerType.Name, _queueName);
                }
            }

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

            // A recovery pass may hold the gate (for instance in its backoff while the broker is down). Wait for it
            // only as long as the host allows: if it does not leave in time, skip the orderly channel close and just
            // dispose; the process is going away and the broker redelivers whatever stays unacknowledged.
            var gateAcquired = false;

            try
            {
                // A free gate is taken at once, whatever the state of the host's token: the token only bounds the
                // wait for a recovery that is actually running.
                gateAcquired = await _recoveryGate.WaitAsync(TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false)
                    || await _recoveryGate.WaitAsync(RecoveryGateStopWait, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            if (!gateAcquired)
            {
                _logger.LogWarning("[RABBIT-FLOW]: Shutdown of {Consumer} on queue '{Queue}' did not get hold of the running recovery in time; closing without the orderly channel close.", _consumerType.Name, _queueName);
            }

            try
            {
                try
                {
                    // Close the channel and wait (bounded) for the broker's close-ok: it confirms the acks of the
                    // drained handlers were processed before the process goes away. Only here is the wait worth it;
                    // recovery paths dispose dead channels off the critical path.
                    var channelToClose = _channel;

                    if (gateAcquired && channelToClose != null && channelToClose.IsOpen)
                    {
                        try
                        {
                            await Task.WhenAny(channelToClose.CloseAsync(cancellationToken: CancellationToken.None), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "[RABBIT-FLOW]: Closing the channel for {Consumer} on queue '{Queue}' at shutdown failed.", _consumerType.Name, _queueName);
                        }
                    }

                    DisposeChannel();
                    DisposeConnection();
                }
                finally
                {
                    if (gateAcquired)
                    {
                        try { _recoveryGate.Release(); } catch (ObjectDisposedException) { }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }

            try { _channelGate.Dispose(); } catch (ObjectDisposedException) { }
            try { _prefetchSemaphore.Dispose(); } catch (ObjectDisposedException) { }
            try { _recoveryGate.Dispose(); } catch (ObjectDisposedException) { }
            _stopCts.Dispose();
        }

        private async Task EnsureConnectionAsync(CancellationToken ct)
        {
            if (_connection != null && _connection.IsOpen)
            {
                return;
            }

            // The connection is gone, so any channel on it is dead too — even though a stale IChannel can keep
            // reporting IsOpen == true for a while after its connection drops. Drop it here so EnsureChannelAsync
            // rebuilds the channel AND re-attaches the consumer on the new connection instead of short-circuiting
            // on the stale flag and leaving the queue with no consumer.
            DisposeChannel();

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

            // Bounded retry around topology declaration + consume. The auto-generated main queue can be present
            // when we declare/adopt it (on the throwaway channel) but already gone by the time we bind/consume it
            // on this channel — e.g. an auto-delete queue whose previous consumer just disconnected, or an
            // x-expires queue — which surfaces as a 404 that closes the channel. On that race we recreate the
            // channel and retry: the next pass finds the queue missing and re-creates it with the current arguments
            // (so there is no longer a mismatch), then binds and consumes. Recovery is suppressed while we rebuild
            // so a channel-close during setup can't kick off a second, racing recovery on top of this loop.
            const int maxAttempts = 3;

            _rebuilding = true;

            try
            {
                for (int attempt = 1; ; attempt++)
                {
                    _channel = await _connection!.CreateChannelAsync(ConfirmChannelOptions, cancellationToken: ct);

                    _channel.CallbackExceptionAsync += OnChannelCallbackExceptionAsync;

                    _channel.ChannelShutdownAsync += OnChannelShutdownAsync;

                    await _channel.BasicQosAsync(0, _prefetch, false, ct);

                    try
                    {
                        if (_autoGenerate && _autoGenObj != null)
                        {
                            await DeclareTopologyAsync(_channel, ct);
                        }

                        var consumer = new AsyncEventingBasicConsumer(_channel);

                        consumer.ReceivedAsync += async (sender, ea) =>
                        {
                            if (_stopping || _lifetimeCt.IsCancellationRequested)
                            {
                                return;
                            }
                            try
                            {
                                await _prefetchSemaphore.WaitAsync(_stopCts.Token);
                            }
                            catch (OperationCanceledException) { return; }
                            catch (ObjectDisposedException) { return; }

                            if (_stopping || _lifetimeCt.IsCancellationRequested)
                            {
                                // The slot was taken just as the stop began: give it back so the drain sees the
                                // semaphore at full capacity instead of waiting its whole cap.
                                try { _prefetchSemaphore.Release(); } catch (ObjectDisposedException) { }

                                return;
                            }

                            // Ack/nack must go to the channel that delivered the message: delivery tags are
                            // per channel, and a delivery that waited for a slot across a channel rebuild would
                            // otherwise settle a different message on the new channel.
                            _ = ProcessMessageAsync(((AsyncEventingBasicConsumer)sender).Channel, ea);
                        };

                        // Broker-initiated basic.cancel arrives here, with the channel still open (see
                        // OnConsumerUnregisteredAsync). This instance never cancels its own consumer, so the event
                        // is only ever raised by the broker.
                        consumer.UnregisteredAsync += OnConsumerUnregisteredAsync;

                        _consumer = consumer;

                        _consumerTag = await _channel.BasicConsumeAsync(queue: _queueName, autoAck: false, consumer: consumer, cancellationToken: ct);

                        return;
                    }
                    catch (OperationInterruptedException ex) when (attempt < maxAttempts && ex.ShutdownReason?.ReplyCode == 404)
                    {
                        _logger.LogWarning(
                            "[RABBIT-FLOW]: Queue '{Queue}' for {Consumer} was deleted by the broker during setup (404 NOT_FOUND) — " +
                            "typically a stale auto-delete or x-expires queue whose previous consumer had just disconnected. " +
                            "Recreating it with the current arguments and retrying (attempt {Attempt}/{Max}).",
                            _queueName, _consumerType.Name, attempt, maxAttempts);

                        DisposeChannel();
                    }
                }
            }
            finally
            {
                _rebuilding = false;
            }
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

            // null = don't send x-queue-type, the broker's default_queue_type decides (see AutoGenerateSettings.QueueType).
            // A type given only through Args["x-queue-type"] counts as the main queue's type for the DLQ as well.
            var queueType = (autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.QueueType))?.GetValue(autoGenObj) as QueueType?)
                ?? QueueTypeExtensions.FromArgument(args != null && args.TryGetValue("x-queue-type", out var rawQueueType) ? rawQueueType : null);

            var deadLetterQueueType = (autoGenObj.GetType().GetProperty(nameof(AutoGenerateSettings<object>.DeadLetterQueueType))?.GetValue(autoGenObj) as QueueType?) ?? queueType;

            if (generateDeadletter)
            {
                _deadLetterQueueName = RabbitFlowTopologyNames.DeadLetterQueue(_queueName);

                _deadLetterExchangeName = RabbitFlowTopologyNames.DeadLetterExchange(_queueName);

                _deadLetterRoutingKey = RabbitFlowTopologyNames.DeadLetterRoutingKey(_queueName);

                _deadLetterTopology = new DeadLetterTopology(durableQueue, durableExchange, autoDeleteQueue, deadLetterQueueType, replicas);

                await DeclareDeadLetterTopologyAsync(channel, _deadLetterTopology, ct);

                args ??= new Dictionary<string, object?>();

                args["x-dead-letter-exchange"] = _deadLetterExchangeName;

                args["x-dead-letter-routing-key"] = _deadLetterRoutingKey;
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

            // An explicit x-queue-type in Args wins (ConsumerSettings.Validate already made sure both agree).
            if (queueType.HasValue && !args.ContainsKey("x-queue-type"))
            {
                args["x-queue-type"] = queueType.Value.ToArgument();
            }

            // x-consumer-timeout only takes effect when the queue is first created: changing Timeout/MaxRetryCount/
            // RetryInterval on an existing queue does NOT update it. Classic queues on 3.x ignore a changed value on
            // redeclare; 4.x quorum queues compare it and answer PRECONDITION_FAILED, which DeclareOrAdoptMainQueueAsync
            // turns into adopting the existing queue. Either way the queue must be deleted and recreated (or a
            // consumer-timeout policy applied) to change it. Not emitted when the handler has no timeout (see
            // ConsumerTimeoutRules), nor once the broker has rejected it (RabbitMQ 4.x classic queues don't accept it
            // on queue.declare; see DeclareOrAdoptMainQueueAsync).
            if (_serverConsumerTimeoutMs.HasValue && !args.ContainsKey("x-consumer-timeout") && !_consumerTimeoutArgRejected)
            {
                args["x-consumer-timeout"] = _serverConsumerTimeoutMs.Value;
            }

            await DeclareOrAdoptQueueAsync(_queueName, durableQueue, exclusiveQueue, autoDeleteQueue, args, ct);

            if (generateExchange)
            {
                await channel.ExchangeDeclareAsync(exchangeName, exchangeType, durableExchange, cancellationToken: ct);

                await channel.QueueBindAsync(_queueName, exchangeName, routingKey, cancellationToken: ct);
            }
        }

        // Declares the dead-letter queue, its exchange, the binding between them and the replica queues. Runs as part
        // of the topology declaration and again, on a throwaway channel, when the broker returns a dead-letter
        // envelope as unroutable because that queue or binding is gone.
        private async Task DeclareDeadLetterTopologyAsync(IChannel channel, DeadLetterTopology topology, CancellationToken ct)
        {
            var deadLetterArgs = new Dictionary<string, object?>();

            if (topology.QueueType.HasValue)
            {
                deadLetterArgs["x-queue-type"] = topology.QueueType.Value.ToArgument();
            }

            // Same declare-or-adopt as the main queue: a dead-letter queue that already exists with another
            // type (or other arguments) must not close the consumer channel and leave the consumer in a
            // recovery loop; it is adopted as-is with a warning.
            await DeclareOrAdoptQueueAsync(_deadLetterQueueName, topology.DurableQueue, false, topology.AutoDeleteQueue, deadLetterArgs, ct);

            await channel.ExchangeDeclareAsync(_deadLetterExchangeName, "direct", topology.DurableExchange, cancellationToken: ct);

            await channel.QueueBindAsync(_deadLetterQueueName, _deadLetterExchangeName, _deadLetterRoutingKey, cancellationToken: ct);

            if (topology.Replicas != null)
            {
                foreach (var replica in topology.Replicas)
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

                    await channel.QueueBindAsync(replicaQueue!, _deadLetterExchangeName, _deadLetterRoutingKey, cancellationToken: ct);
                }
            }
        }

        private sealed class DeadLetterTopology
        {
            public DeadLetterTopology(bool durableQueue, bool durableExchange, bool autoDeleteQueue, QueueType? queueType, System.Collections.IEnumerable? replicas)
            {
                DurableQueue = durableQueue;
                DurableExchange = durableExchange;
                AutoDeleteQueue = autoDeleteQueue;
                QueueType = queueType;
                Replicas = replicas;
            }

            public bool DurableQueue { get; }

            public bool DurableExchange { get; }

            public bool AutoDeleteQueue { get; }

            public QueueType? QueueType { get; }

            public System.Collections.IEnumerable? Replicas { get; }
        }

        // Declares a queue (the main queue or the dead-letter queue) on a throwaway channel so an argument mismatch
        // (PRECONDITION_FAILED) can't take the consumer's channel down with it. If the queue is missing it is created
        // with these arguments; if it already exists with identical arguments the declare is a harmless no-op. A
        // genuine mismatch reaches the catch for arguments RabbitMQ verifies for equivalence (x-queue-type,
        // x-dead-letter-exchange/-routing-key, x-max-priority, custom Args, and x-consumer-timeout on 4.x quorum
        // queues; 3.x classic queues ignore a changed x-consumer-timeout instead). On mismatch we adopt the existing
        // queue and keep running rather than crashing the consumer: a running consumer beats one that won't start and
        // silently leaves the system idle.
        private async Task DeclareOrAdoptQueueAsync(string queueName, bool durable, bool exclusive, bool autoDelete, IDictionary<string, object?> args, CancellationToken ct)
        {
            // Each fragile declare runs on its own throwaway channel so a 406 can't take the consumer's channel down.
            while (true)
            {
                try
                {
                    using var declareChannel = await _connection!.CreateChannelAsync(cancellationToken: ct).ConfigureAwait(false);

                    await declareChannel.QueueDeclareAsync(queueName, durable, exclusive, autoDelete, args, cancellationToken: ct).ConfigureAwait(false);

                    return;
                }
                catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 406)
                {
                    var brokerReason = ex.ShutdownReason?.ReplyText;

                    // RabbitMQ 4.x rejects the per-queue 'x-consumer-timeout' argument that 3.13 accepted
                    // ("PRECONDITION_FAILED - invalid arg 'x-consumer-timeout' ... of queue type rabbit_classic_queue").
                    // It's a derived convenience, not user data, so drop it and re-declare: the broker-wide
                    // consumer_timeout applies instead, and a longer per-queue timeout can still be set with a
                    // `consumer_timeout` policy. This keeps AutoGenerate working across 3.x and 4.x. An "inequivalent
                    // arg 'x-consumer-timeout'" (4.x quorum queue that already exists with another value) is a
                    // mismatch, handled below like any other argument.
                    if (args.ContainsKey("x-consumer-timeout")
                        && (brokerReason?.IndexOf("invalid arg", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                        && (brokerReason?.IndexOf("x-consumer-timeout", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
                    {
                        _logger.LogWarning(
                            "[RABBIT-FLOW]: Broker rejected the 'x-consumer-timeout' queue argument for '{Queue}' ({Consumer}); " +
                            "this RabbitMQ version does not accept it on classic queues. Re-declaring '{Queue}' without it — on 4.0–4.2 the " +
                            "broker-wide consumer_timeout applies, on 4.3+ classic queues have no consumer timeout at all. Use a quorum queue " +
                            "(QueueType = Quorum) if you need a broker-enforced per-queue timeout. Broker reason: {Reason}",
                            queueName, _consumerType.Name, queueName, brokerReason);

                        args.Remove("x-consumer-timeout");

                        _consumerTimeoutArgRejected = true;

                        continue;
                    }

                    // Otherwise a 406 means one of two different things:
                    //   (a) the queue ALREADY EXISTS with arguments that differ from ours, or
                    //   (b) the broker REJECTED the declare (an argument it won't accept, or a policy that conflicts
                    //       with the requested arguments/queue-type) and the queue was NOT created.
                    // We must not treat (b) as (a): adopting then binding/consuming a queue that doesn't exist yields a
                    // misleading "already exists" warning followed by a 404 loop. A passive declare (which never
                    // compares arguments) tells them apart.
                    bool exists;

                    try
                    {
                        using var probe = await _connection!.CreateChannelAsync(cancellationToken: ct).ConfigureAwait(false);

                        await probe.QueueDeclarePassiveAsync(queueName, ct).ConfigureAwait(false);

                        exists = true;
                    }
                    catch (OperationInterruptedException probeEx) when (probeEx.ShutdownReason?.ReplyCode == 404)
                    {
                        exists = false;
                    }

                    if (exists)
                    {
                        // (a) Adopt the existing queue as-is and keep running against its current arguments.
                        _logger.LogWarning(
                            "[RABBIT-FLOW]: Queue '{Queue}' already exists with arguments that differ from the current configuration " +
                            "(e.g. x-queue-type, x-dead-letter-exchange/-routing-key, x-max-priority, or custom Args). " +
                            "The consumer for {Consumer} will keep running against the EXISTING queue and its current arguments. " +
                            "To apply the new arguments, delete queue '{Queue}' and let the consumer recreate it. Broker reason: {Reason}",
                            queueName, _consumerType.Name, queueName, brokerReason);

                        return;
                    }

                    // (b) The declare was rejected and the queue does not exist. Surface the broker's actual reason
                    // instead of pretending to adopt; binding/consuming would only fail with 404. The caller's setup
                    // fails cleanly (the host still starts; recovery keeps retrying), and the operator gets the real cause.
                    throw new RabbitFlowException(
                        $"[RABBIT-FLOW]: Failed to declare queue '{queueName}' for {_consumerType.Name}: the broker rejected the " +
                        $"declare with PRECONDITION_FAILED and the queue does not exist. This is NOT an \"already exists\" mismatch — " +
                        $"it usually means an argument value is not accepted by the broker, or a broker policy conflicts with the " +
                        $"requested arguments or queue-type. Broker reason: {brokerReason}", ex);
                }
                catch (OperationInterruptedException ex)
                {
                    // Any other broker-side rejection of the declare (e.g. 405 RESOURCE_LOCKED, 530 NOT_ALLOWED,
                    // 541 deprecated-feature like transient_nonexcl_queues, ...). Never swallow the broker's reason:
                    // log the code and ReplyText with context, then rethrow so setup fails cleanly and recovery retries.
                    _logger.LogError(ex,
                        "[RABBIT-FLOW]: Unexpected broker error declaring queue '{Queue}' for {Consumer}. ReplyCode: {Code}, Broker reason: {Reason}",
                        queueName, _consumerType.Name, ex.ShutdownReason?.ReplyCode, ex.ShutdownReason?.ReplyText);

                    throw;
                }
            }
        }

        private async Task ProcessMessageAsync(IChannel channel, BasicDeliverEventArgs args)
        {
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

                var deliveryCount = RabbitFlowHeaders.ReadDeliveryCount(args.BasicProperties?.Headers);

                // Every log line written while this delivery is processed — the library's and the handler's — carries
                // the message identity, so a handler's own logs can be tied to the queue and message without a tracer.
                using var logScope = _logger.BeginScope(new Dictionary<string, object?>
                {
                    ["Queue"] = _queueName,
                    ["Consumer"] = _consumerType.Name,
                    ["MessageId"] = msgId,
                    ["CorrelationId"] = corrId,
                    ["DeliveryTag"] = args.DeliveryTag
                });

                using var activity = RabbitFlowDiagnostics.StartProcess(_queueName, args.BasicProperties?.Headers, msgId, corrId, args.Redelivered, args.DeliveryTag, deliveryCount);

                var startTimestamp = Stopwatch.GetTimestamp();

                void RecordConsumeMetrics(string outcome, string? reason = null)
                {
                    var elapsedSeconds = (Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency;
                    var queueTag = new KeyValuePair<string, object?>("queue", _queueName);
                    var outcomeTag = new KeyValuePair<string, object?>("outcome", outcome);

                    if (reason != null)
                    {
                        RabbitFlowDiagnostics.ConsumedMessages.Add(1, queueTag, outcomeTag, new KeyValuePair<string, object?>("reason", reason));
                    }
                    else
                    {
                        RabbitFlowDiagnostics.ConsumedMessages.Add(1, queueTag, outcomeTag);
                    }

                    RabbitFlowDiagnostics.ConsumeDuration.Record(elapsedSeconds, queueTag, outcomeTag);
                }

                // A delivery the broker keeps taking back (quorum queues count it in x-delivery-count) returns to the
                // head of the queue each time and blocks it; give up on it before the broker's delivery-limit drops it
                // raw. Dead-lettered with the envelope as a permanent failure, so the reprocessor parks it.
                if (ConsumerPoisonGuard.ShouldGiveUp(_maxBrokerDeliveries, deliveryCount, args.Redelivered, _autoAckOnError, !string.IsNullOrEmpty(_deadLetterQueueName)))
                {
                    _logger.LogWarning(
                        "[RABBIT-FLOW]: Message {MessageId} on queue '{Queue}' was returned by the broker {DeliveryCount} time(s) (x-delivery-count), reaching MaxBrokerDeliveries = {Max} for {Consumer}. " +
                        "Dead-lettering it with its envelope without invoking the handler so it stops blocking the queue; the reprocessor parks it.",
                        msgId, _queueName, deliveryCount.GetValueOrDefault(), _maxBrokerDeliveries.GetValueOrDefault(), _consumerType.Name);

                    RabbitFlowDiagnostics.PoisonedMessages.Add(1, new KeyValuePair<string, object?>("queue", _queueName));

                    var poison = new RabbitFlowPoisonMessageException(deliveryCount.GetValueOrDefault(), _maxBrokerDeliveries.GetValueOrDefault());

                    RabbitFlowDiagnostics.RecordFailure(activity, poison);
                    RecordConsumeMetrics("failure", ConsumerFailureReasons.Poison);
                    await HandleErrorAsync(channel, args.DeliveryTag, null, body, reprocessAttempts, msgId, corrId, args.BasicProperties, poison, rootCt);
                    return;
                }

                object? evt;

                try
                {
                    evt = _markerFactory.Deserialize(body, _serializerOptions) ?? throw new Exception("Deserialization returned null");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "[RABBIT-FLOW]: Message {MessageId} on queue '{Queue}' could not be deserialized as {EventType} for {Consumer}; dead-lettering it. CorrelationId={CorrelationId}, BodyBytes={BodyBytes}.",
                        msgId, _queueName, _eventType.Name, _consumerType.Name, corrId, body.Length);

                    RabbitFlowDiagnostics.RecordFailure(activity, ex);
                    RecordConsumeMetrics("failure", ConsumerFailureReasons.Deserialize);
                    await HandleErrorAsync(channel, args.DeliveryTag, null, body, reprocessAttempts, msgId, corrId, args.BasicProperties, ex, rootCt);
                    return;
                }

                int remainingAttempts = _totalAttempts;

                int attemptsMade = 0;

                Exception? lastException = null;

                while (remainingAttempts > 0 && !rootCt.IsCancellationRequested)
                {
                    attemptsMade++;

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
                            deliveryCount,
                            ReadDeliveryMode(args.BasicProperties),
                            ReadType(args.BasicProperties),
                            ReadAppId(args.BasicProperties),
                            ReadExpiration(args.BasicProperties),
                            ReadPriority(args.BasicProperties),
                            ReadTimestamp(args.BasicProperties),
                            ReadReplyTo(args.BasicProperties),
                            ReadContentType(args.BasicProperties));

                        var consumerInstance = scope.ServiceProvider.GetRequiredService(_consumerType);

                        var handleTask = _markerFactory.InvokeHandleAsync(consumerInstance, evt, messageContext, attemptCt);

                        // Timeout <= 0 (e.g. Timeout.InfiniteTimeSpan) means "no timeout": the attempt token never
                        // fires, so there is no cancellation for the handler to ignore and the watchdog must not run —
                        // _timeout + grace would otherwise abandon a healthy long-running handler after ~30s.
                        if (_timeout > TimeSpan.Zero && !handleTask.IsCompleted)
                        {
                            using var abandonCts = new CancellationTokenSource();

                            var winner = await Task.WhenAny(handleTask, Task.Delay(_timeout + HandlerAbandonGrace, abandonCts.Token)).ConfigureAwait(false);

                            if (winner != handleTask)
                            {
                                // The handler ignored its cancelled attempt token. Abandon the wait so the prefetch
                                // slot is reclaimed and the message is dead-lettered; the detached task keeps running
                                // (and may fault once this iteration's DI scope is disposed) — observe it so its
                                // eventual failure is logged instead of raising UnobservedTaskException.
                                ObserveAbandonedHandler(handleTask);

                                throw new RabbitFlowHandlerAbandonedException(_consumerType.Name, _timeout, HandlerAbandonGrace);
                            }

                            abandonCts.Cancel();
                        }

                        await handleTask.ConfigureAwait(false);

                        await SafeAckAsync(channel, args.DeliveryTag, rootCt);

                        RecordConsumeMetrics("success");

                        return;
                    }
                    catch (OperationCanceledException ex) when (attemptCts.IsCancellationRequested && !rootCt.IsCancellationRequested)
                    {
                        lastException = ex;

                        LogRetry(ex, remainingAttempts, ConsumerFailureReasons.Timeout, msgId);

                        await ApplyRetryDelay(remainingAttempts, rootCt);
                    }
                    catch (Exception ex) when (TransientExceptionClassifier.IsTransient(ex))
                    {
                        lastException = ex;

                        LogRetry(ex, remainingAttempts, ConsumerFailureReasons.HandlerError, msgId);

                        await ApplyRetryDelay(remainingAttempts, rootCt);
                    }
                    catch (Exception) when (rootCt.IsCancellationRequested)
                    {
                        // The host is going away: not a failure of the message. Leave it unacknowledged so the
                        // broker redelivers it after the channel closes.
                        return;
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
                    var failure = lastException ?? new Exception("Unknown error");

                    var reason = ConsumerFailureReasons.Of(failure);

                    _logger.LogError(failure,
                        "[RABBIT-FLOW]: Message {MessageId} on queue '{Queue}' failed for {Consumer} after {Attempts} attempt(s) ({Reason}); {Disposition}. " +
                        "CorrelationId={CorrelationId}, Redelivered={Redelivered}, DeliveryCount={DeliveryCount}, ReprocessAttempts={ReprocessAttempts}.",
                        msgId, _queueName, _consumerType.Name, attemptsMade, reason, FailureDisposition(),
                        corrId, args.Redelivered, deliveryCount, reprocessAttempts);

                    RabbitFlowDiagnostics.RecordFailure(activity, failure);
                    RecordConsumeMetrics("failure", reason);
                    await HandleErrorAsync(channel, args.DeliveryTag, evt, body, reprocessAttempts, msgId, corrId, args.BasicProperties, failure, rootCt);
                }
            }
            catch (AlreadyClosedException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RABBIT-FLOW]: Unhandled exception while processing a delivery on queue '{Queue}' for {Consumer}; returning it to the queue.", _queueName, _consumerType.Name);

                // Never leave the delivery unsettled: it would hold a prefetch credit until the channel closes.
                // Requeue rather than drop — this path means a library fault, not a bad message.
                try
                {
                    await _channelGate.WaitAsync(rootCt).ConfigureAwait(false);

                    try
                    {
                        if (channel.IsOpen)
                        {
                            await channel.BasicNackAsync(args.DeliveryTag, false, true, rootCt);
                        }
                    }
                    finally
                    {
                        _channelGate.Release();
                    }
                }
                catch (Exception nackEx)
                {
                    _logger.LogDebug(nackEx, "[RABBIT-FLOW]: Could not return the delivery to queue '{Queue}'; the broker redelivers it when the channel closes.", _queueName);
                }
            }
            finally
            {
                try { _prefetchSemaphore.Release(); } catch (ObjectDisposedException) { }
            }
        }

        // Publishes the dead-letter envelope persistent, so it survives a broker restart while it waits in the durable
        // DLQ for the next reprocessor cycle (which can be up to Interval — hours — away), and mandatory, so a missing
        // dead-letter queue or binding surfaces as a PublishException with IsReturn instead of a confirmed drop. Fresh
        // properties per call: the client stamps its publish sequence number on them to correlate a basic.return.
        private Task PublishEnvelopeAsync(IChannel channel, string? messageId, string? correlationId, byte[] bytes, CancellationToken ct)
        {
            var properties = new BasicProperties
            {
                DeliveryMode = DeliveryModes.Persistent,
                MessageId = messageId,
                CorrelationId = correlationId
            };

            return channel.BasicPublishAsync(exchange: _deadLetterExchangeName, routingKey: _deadLetterRoutingKey, mandatory: true, basicProperties: properties, body: bytes, cancellationToken: ct).AsTask();
        }

        // What HandleErrorAsync is going to do with a failed delivery, for the terminal log line.
        private string FailureDisposition()
        {
            if (_autoAckOnError)
            {
                return "acknowledging it (AutoAckOnError)";
            }

            if (_extendDeadLetter && !string.IsNullOrEmpty(_deadLetterQueueName))
            {
                return "dead-lettering it with its envelope";
            }

            return "rejecting it without requeue (the broker dead-letters it only if the queue has a dead-letter exchange, otherwise it is dropped)";
        }

        // Logged at Warning: the message is not lost, the next attempt follows after RetryInterval. The terminal
        // failure, if it comes, is logged at Error with the full context.
        private void LogRetry(Exception ex, int remainingAttempts, string reason, string? messageId)
        {
            if (remainingAttempts <= 1)
            {
                return;
            }

            RabbitFlowDiagnostics.RetriedMessages.Add(1, new KeyValuePair<string, object?>("queue", _queueName));

            var attempt = _totalAttempts - remainingAttempts + 1;

            _logger.LogWarning(ex,
                "[RABBIT-FLOW]: Attempt {Attempt}/{Total} for message {MessageId} on queue '{Queue}' failed ({Reason}) for {Consumer}; retrying in {DelayMs}ms.",
                attempt, _totalAttempts, messageId, _queueName, reason, _consumerType.Name, _rpInterval);
        }

        private void ObserveAbandonedHandler(Task handleTask)
        {
            _ = handleTask.ContinueWith(
                t => _logger.LogError(t.Exception, "[RABBIT-FLOW]: Abandoned handler for {Consumer} eventually faulted (its DI scope may already have been disposed).", _consumerType.Name),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
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
            catch (Exception ex)
            {
                // The handler's work is done but the broker never learns it: the delivery is redelivered after
                // the channel closes (at-least-once). Logged inside the delivery's scope, so the message id is attached.
                _logger.LogWarning(ex, "[RABBIT-FLOW]: Acknowledging a processed delivery on queue '{Queue}' for {Consumer} failed; the broker will redeliver it.", _queueName, _consumerType.Name);
            }
            finally
            {
                _channelGate.Release();
            }
        }

        private async Task HandleErrorAsync(IChannel channel, ulong deliveryTag, object? evt, byte[]? body, int reprocessAttempts, string? messageId, string? correlationId, IReadOnlyBasicProperties? originalProperties, Exception exception, CancellationToken ct)
        {
            var requeueAfterReturn = false;

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

                    var returned = false;

                    if (bytes != null)
                    {
                        try
                        {
                            try
                            {
                                await PublishEnvelopeAsync(channel, messageId, correlationId, bytes, ct);
                            }
                            catch (PublishException ex) when (ex.IsReturn && _deadLetterTopology != null)
                            {
                                // The broker had no queue to route the envelope to: the dead-letter queue or its binding
                                // is gone (deleted by hand, expired, auto-deleted). A confirm alone would not tell — the
                                // broker confirms an unroutable message it dropped — which is why the envelope travels
                                // mandatory. Rebuild the dead-letter topology on this channel and publish once more.
                                // From here on any failure must keep the message in the queue: the broker's own
                                // dead-letter route is the one that just proved missing.
                                returned = true;

                                _logger.LogWarning(ex, "[RABBIT-FLOW]: Dead-letter envelope for {Consumer} was returned by the broker as unroutable; re-declaring the dead-letter topology of '{Queue}' and publishing it again.", _consumerType.Name, _queueName);

                                // On a throwaway channel: a declare the broker refuses (406 on a re-created exchange or
                                // replica, 404 on a binding) closes the channel it ran on, and that must not be the
                                // consumer channel with its in-flight deliveries. Bindings are not tied to a channel.
                                using (var declareChannel = await _connection!.CreateChannelAsync(cancellationToken: ct).ConfigureAwait(false))
                                {
                                    await DeclareDeadLetterTopologyAsync(declareChannel, _deadLetterTopology, ct);
                                }

                                await PublishEnvelopeAsync(channel, messageId, correlationId, bytes, ct);
                            }

                            published = true;
                        }
                        catch (PublishException ex) when (ex.IsReturn)
                        {
                            _logger.LogError(ex, "[RABBIT-FLOW]: Dead-letter envelope for {Consumer} is still unroutable after re-declaring the dead-letter topology; returning the message to queue '{Queue}' for a later delivery.", _consumerType.Name, _queueName);
                        }
                        catch (Exception ex) when (returned)
                        {
                            _logger.LogError(ex, "[RABBIT-FLOW]: Re-declaring the dead-letter topology of '{Queue}' after a returned envelope failed for {Consumer}; returning the message to the queue for a later delivery.", _queueName, _consumerType.Name);
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
                    else if (returned)
                    {
                        // Broker-native dead-lettering would meet the same missing route and drop the raw message, so
                        // the delivery goes back to the queue instead, once this gate is released: the next delivery
                        // runs the handler and the envelope again, once per redelivery until the topology is back. The
                        // library never drops it; on a quorum queue the broker's own delivery-limit still bounds how
                        // many redeliveries it allows.
                        requeueAfterReturn = true;
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

            if (requeueAfterReturn)
            {
                await RequeueAfterReturnedEnvelopeAsync(channel, deliveryTag, ct);
            }
        }

        // Returns a delivery whose envelope the broker could not route to the queue, after a pause and outside the
        // channel gate so the acks of other handlers are not held up. Without the pause a fast-failing handler and a
        // route that stays broken would spin (handler, envelope, return, requeue) at the speed of a few broker round
        // trips while holding the head of the queue.
        private async Task RequeueAfterReturnedEnvelopeAsync(IChannel channel, ulong deliveryTag, CancellationToken ct)
        {
            try
            {
                await Task.Delay(_recoveryBaseDelayMs, _stopCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down: left unacknowledged, the broker redelivers it when the channel closes.
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                await _channelGate.WaitAsync(ct).ConfigureAwait(false);

                try
                {
                    if (channel.IsOpen)
                    {
                        await channel.BasicNackAsync(deliveryTag, false, true, ct);
                    }
                }
                finally
                {
                    _channelGate.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[RABBIT-FLOW]: Could not return the delivery to queue '{Queue}'; the broker redelivers it when the channel closes.", _queueName);
            }
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

                    if (RabbitFlowHeaders.IsBrokerOwned(kvp.Key))
                    {
                        continue; // x-delivery-count, x-death, ...: a re-enqueued copy must start its own history
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

            if (_stopping || _rebuilding || !_recoveryEnabled || args.Initiator == ShutdownInitiator.Application)
            {
                return Task.CompletedTask;
            }

            _ = TriggerRecoveryAsync();

            return Task.CompletedTask;
        }

        // The broker cancels a consumer (basic.cancel) without closing its channel when the queue is deleted, when
        // the node hosting the queue goes away, or — on RabbitMQ 4.3+ quorum queues — when a delivery exceeds the
        // consumer acknowledgement timeout. The channel and connection stay open, so neither shutdown handler fires
        // and the client has already removed the consumer from the channel: left alone, the queue keeps a live
        // channel that consumes nothing until the process restarts. Recovery must rebuild the channel, because
        // EnsureChannelAsync short-circuits on an open one. The channel is disposed off this callback: closing it
        // waits for the consumer dispatcher to drain, and this handler runs on that dispatcher.
        private Task OnConsumerUnregisteredAsync(object sender, ConsumerEventArgs args)
        {
            var cancelledConsumer = sender as AsyncEventingBasicConsumer;

            var cancelledChannel = cancelledConsumer?.Channel;

            // A consumer whose channel is already closed is the shutdown path's business, not a broker cancel.
            if (_stopping || (cancelledChannel != null && !cancelledChannel.IsOpen))
            {
                return Task.CompletedTask;
            }

            _logger.LogWarning(
                "[RABBIT-FLOW]: Broker cancelled the consumer for {Consumer} on queue '{Queue}' (consumer tag(s): {Tags}) without closing the channel — " +
                "the queue was deleted, its node went away, or a delivery exceeded the broker's consumer timeout. {Action}",
                _consumerType.Name, _queueName, string.Join(",", args.ConsumerTags ?? Array.Empty<string>()),
                _recoveryEnabled ? "Rebuilding the channel and re-subscribing." : "Recovery is disabled (AutomaticRecoveryEnabled = false); the consumer stays down until restart.");

            RabbitFlowDiagnostics.ConsumerCancellations.Add(1, new KeyValuePair<string, object?>("queue", _queueName));

            if (!_recoveryEnabled)
            {
                return Task.CompletedTask;
            }

            _ = Task.Run(async () =>
            {
                // Drop only the cancelled consumer's channel, atomically: a recovery already in flight may have
                // replaced _channel with a fresh one, and that one must stay. Deliveries still in flight on the
                // cancelled channel were already returned to the queue by the broker, so their late acks cannot
                // succeed anyway; they are redelivered (at-least-once).
                if (cancelledChannel != null
                    && ReferenceEquals(Interlocked.CompareExchange(ref _channel, null, cancelledChannel), cancelledChannel))
                {
                    if (ReferenceEquals(_consumer, cancelledConsumer))
                    {
                        _consumer = null;
                    }

                    cancelledConsumer!.UnregisteredAsync -= OnConsumerUnregisteredAsync;

                    DisposeChannelInstance(cancelledChannel);
                }

                await TriggerRecoveryAsync().ConfigureAwait(false);
            });

            return Task.CompletedTask;
        }

        private Task OnConnectionShutdownAsync(object sender, ShutdownEventArgs args)
        {
            _logger.LogWarning("[RABBIT-FLOW]: Connection shutdown for {Consumer}. ReplyCode: {Code}, ReplyText: {Text}", _consumerType.Name, args.ReplyCode, args.ReplyText);

            if (_stopping || _rebuilding || !_recoveryEnabled || args.Initiator == ShutdownInitiator.Application)
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
            if (_stopping || !_recoveryEnabled)
            {
                return;
            }

            // Cancelled both by StopAsync and by a host that aborts startup; taken now because StopAsync disposes
            // the source once it is done.
            CancellationToken stopToken;

            try
            {
                stopToken = _stopCts.Token;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                if (!_recoveryGate.TryEnter())
                {
                    // Another recovery is running: the gate recorded this request, so the holder runs once more
                    // before leaving (or triggers a new pass on its way out) and a cancel or a shutdown that
                    // arrives after it already built its channel is not lost.
                    return;
                }
            }
            catch (ObjectDisposedException)
            {
                // StopAsync disposed the gate while a detached recovery trigger was still in flight.
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
                        _recoveryGate.BeginPass();

                        await EnsureConnectionAsync(stopToken);

                        await EnsureChannelAsync(stopToken);

                        if (_stopping)
                        {
                            // Shutdown gave up waiting for this pass and already disposed what it could; whatever
                            // this pass built after that would otherwise outlive the consumer.
                            DisposeChannel();
                            DisposeConnection();
                            return;
                        }

                        if (_recoveryGate.TakePending())
                        {
                            // Something invalidated the channel while this pass ran; both Ensure* calls are
                            // idempotent, so go round again.
                            continue;
                        }

                        _logger.LogInformation("[RABBIT-FLOW]: Consumer {Consumer} recovered after {Attempts} attempt(s).", _consumerType.Name, attempt);
                        return;
                    }
                    catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        var cap = Math.Max(30_000, _recoveryBaseDelayMs);
                        var delay = Math.Min(_recoveryBaseDelayMs * (int)Math.Pow(2, Math.Min(attempt - 1, 6)), cap);

                        // The first attempts after a broker restart are expected to fail; only a recovery that keeps
                        // failing is worth an Error.
                        _logger.Log(attempt < 3 ? LogLevel.Warning : LogLevel.Error, ex,
                            "[RABBIT-FLOW]: Recovery attempt {Attempt} failed for {Consumer} on queue '{Queue}'. Retrying in {DelayMs}ms.", attempt, _consumerType.Name, _queueName, delay);

                        try { await Task.Delay(delay, stopToken); } catch (OperationCanceledException) { return; }
                    }
                }
            }
            finally
            {
                var pending = false;

                try
                {
                    pending = _recoveryGate.Exit();
                }
                catch (ObjectDisposedException)
                {
                }

                if (pending && !_stopping)
                {
                    // A request arrived after this pass's last check and found the gate still held, so nobody
                    // else will honour it: start the next pass from here.
                    _ = TriggerRecoveryAsync();
                }
            }
        }

        private void DisposeChannel()
        {
            var ch = _channel;

            _channel = null;

            var consumer = _consumer;

            _consumer = null;

            if (consumer != null)
            {
                consumer.UnregisteredAsync -= OnConsumerUnregisteredAsync;
            }

            if (ch == null)
            {
                return;
            }

            DisposeChannelInstance(ch);
        }

        // Unhooks this instance's handlers and disposes the channel off the calling path: closing a channel whose
        // connection the broker force-closed waits for the client's ContinuationTimeout (20s) for a close-ok that
        // never comes, and nothing here needs the close to complete.
        private void DisposeChannelInstance(IChannel ch)
        {
            ch.CallbackExceptionAsync -= OnChannelCallbackExceptionAsync;

            ch.ChannelShutdownAsync -= OnChannelShutdownAsync;

            _ = Task.Run(async () =>
            {
                try
                {
                    await ch.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[RABBIT-FLOW]: Disposing the previous channel for {Consumer} on queue '{Queue}' failed; the channel was already dead.", _consumerType.Name, _queueName);
                }
            });
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

            // Disposing a connection that the broker force-closed (or that died mid-flight) blocks the caller for
            // up to the client's ContinuationTimeout (20s by default) while it waits for a close-ok that never
            // arrives. Offload it so recovery and shutdown aren't stalled; a healthy connection still disposes
            // promptly, just off the calling path.
            var connToDispose = conn;

            _ = Task.Run(async () =>
            {
                try
                {
                    await connToDispose.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[RABBIT-FLOW]: Disposing the previous connection for {Consumer} on queue '{Queue}' failed; the connection was already dead.", _consumerType.Name, _queueName);
                }
            });
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
