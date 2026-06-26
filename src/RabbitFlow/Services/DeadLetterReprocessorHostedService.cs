using EasyRabbitFlow.Exceptions;
using EasyRabbitFlow.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
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
    /// <summary>
    /// Hosted service that periodically drains the auto-generated dead-letter queue of every consumer
    /// configured with <see cref="DeadLetterReprocessSettings{TConsumer}"/> and re-publishes transient
    /// failures back to the main queue until the configured maximum attempt count is reached.
    /// Messages that will never be re-enqueued (exhausted, permanent, malformed) are moved once to the
    /// parking queue (<c>{queue}-deadletter-parking</c>) so they don't churn through the DLQ on every cycle.
    /// </summary>
    internal sealed class DeadLetterReprocessorHostedService : IHostedService
    {
        private readonly IServiceProvider _root;
        private readonly ILogger<DeadLetterReprocessorHostedService> _logger;
        private readonly List<DeadLetterReprocessWorker> _workers = new List<DeadLetterReprocessWorker>();
        private CancellationTokenSource? _stoppingCts;

        public DeadLetterReprocessorHostedService(IServiceProvider root, ILogger<DeadLetterReprocessorHostedService> logger)
        {
            _root = root;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            var markers = _root.GetServices<IConsumerSettingsMarker>().ToList();

            if (markers.Count == 0) 
                return Task.CompletedTask;

            _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var connectionFactory = _root.GetRequiredService<ConnectionFactory>();

            var serializerOptions = _root.GetKeyedService<JsonSerializerOptions>("RabbitFlowJsonSerializer") ?? new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            foreach (var marker in markers)
            {
                var settings = marker.SettingsInstance;

                if (!settings.Enable) continue;

                if (!settings.AutoGenerate) continue;

                if (string.IsNullOrWhiteSpace(settings.QueueName)) continue;

                var reprocessObj = _root.GetService(typeof(DeadLetterReprocessSettings<>).MakeGenericType(marker.ConsumerType));

                if (reprocessObj == null) continue;

                var rpType = reprocessObj.GetType();

                var enabled = (bool)rpType.GetProperty(nameof(DeadLetterReprocessSettings<object>.Enabled))!.GetValue(reprocessObj)!;

                if (!enabled) continue;

                var maxAttempts = (int)rpType.GetProperty(nameof(DeadLetterReprocessSettings<object>.MaxReprocessAttempts))!.GetValue(reprocessObj)!;
                var interval = (TimeSpan)rpType.GetProperty(nameof(DeadLetterReprocessSettings<object>.Interval))!.GetValue(reprocessObj)!;
                var maxMessagesPerCycle = (int)rpType.GetProperty(nameof(DeadLetterReprocessSettings<object>.MaxMessagesPerCycle))!.GetValue(reprocessObj)!;

                var worker = new DeadLetterReprocessWorker(
                    _logger,
                    connectionFactory,
                    serializerOptions,
                    consumerName: marker.ConsumerType.Name,
                    queueName: settings.QueueName,
                    maxAttempts: maxAttempts,
                    interval: interval,
                    maxMessagesPerCycle: maxMessagesPerCycle);

                _workers.Add(worker);

                worker.Start(_stoppingCts.Token);

                _logger.LogInformation("[RABBIT-FLOW]: Dead-letter reprocessor enabled for {Consumer}. Queue={Queue}, Interval={Interval}, MaxAttempts={MaxAttempts}, MaxMessagesPerCycle={MaxPerCycle}",
                    marker.ConsumerType.Name, settings.QueueName, interval, maxAttempts, maxMessagesPerCycle);
            }

            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_stoppingCts != null)
            {
                try { _stoppingCts.Cancel(); } catch { }
            }

            foreach (var worker in _workers)
            {
                await worker.WaitForCompletionAsync();
            }

            _stoppingCts?.Dispose();
        }
    }

    internal sealed class DeadLetterReprocessWorker
    {
        private readonly ILogger _logger;
        private readonly ConnectionFactory _connectionFactory;
        private readonly JsonSerializerOptions _serializerOptions;
        private readonly string _consumerName;
        private readonly string _queueName;
        private readonly string _deadLetterQueueName;
        private readonly string _deadLetterExchangeName;
        private readonly string _deadLetterRoutingKey;
        private readonly string _parkingQueueName;
        private readonly int _maxAttempts;
        private readonly TimeSpan _interval;
        private readonly int _maxMessagesPerCycle;

        private Task? _runTask;

        private bool _parkingQueueExistsLogged;

        // Publisher confirms for the working channel: every re-enqueue/park is a publish-then-ack pair, and we must
        // know the publish reached the broker before acking it off the DLQ. With tracking enabled BasicPublishAsync
        // awaits the broker ack and throws on failure, so an unconfirmed publish aborts before the ack and the
        // message stays safely in the DLQ instead of being lost.
        private static readonly CreateChannelOptions ConfirmChannelOptions =
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);

        public DeadLetterReprocessWorker(
            ILogger logger,
            ConnectionFactory connectionFactory,
            JsonSerializerOptions serializerOptions,
            string consumerName,
            string queueName,
            int maxAttempts,
            TimeSpan interval,
            int maxMessagesPerCycle)
        {
            _logger = logger;
            _connectionFactory = connectionFactory;
            _serializerOptions = serializerOptions;
            _consumerName = consumerName;
            _queueName = queueName;
            _deadLetterQueueName = RabbitFlowTopologyNames.DeadLetterQueue(queueName);
            _deadLetterExchangeName = RabbitFlowTopologyNames.DeadLetterExchange(queueName);
            _deadLetterRoutingKey = RabbitFlowTopologyNames.DeadLetterRoutingKey(queueName);
            _parkingQueueName = RabbitFlowTopologyNames.ParkingQueue(queueName);
            _maxAttempts = maxAttempts;
            _interval = interval;
            _maxMessagesPerCycle = maxMessagesPerCycle;
        }

        public void Start(CancellationToken cancellationToken)
        {
            _runTask = Task.Run(() => RunAsync(cancellationToken), cancellationToken);
        }

        public Task WaitForCompletionAsync() => _runTask ?? Task.CompletedTask;

        private async Task RunAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try 
                    { 
                        await Task.Delay(_interval, ct).ConfigureAwait(false); 
                    }
                    catch (OperationCanceledException) 
                    { 
                        return; 
                    }

                    try
                    {
                        await RunCycleAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) 
                    { 
                        return; 
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[RABBIT-FLOW]: Dead-letter reprocessor cycle failed for {Consumer}.", _consumerName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RABBIT-FLOW]: Dead-letter reprocessor worker crashed for {Consumer}.", _consumerName);
            }
        }

        internal async Task RunCycleAsync(CancellationToken ct)
        {
            using var connection = await _connectionFactory.CreateConnectionAsync($"reprocessor_{_queueName}", ct).ConfigureAwait(false);

            using var channel = await connection.CreateChannelAsync(ConfirmChannelOptions, cancellationToken: ct).ConfigureAwait(false);

            uint initialCount;

            try
            {
                initialCount = await channel.MessageCountAsync(_deadLetterQueueName, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RABBIT-FLOW]: Reprocessor could not read DLQ length for {Dlq}. Skipping cycle.", _deadLetterQueueName);
                return;
            }

            if (initialCount == 0)
            {
                _logger.LogDebug("[RABBIT-FLOW]: Reprocessor for {Consumer}: DLQ {Dlq} is empty.", _consumerName, _deadLetterQueueName);
                return;
            }

            // Parking queue for messages that will never be re-enqueued (exhausted, permanent, malformed), so they
            // don't churn through the DLQ on every cycle. Ensured only now that the DLQ actually has messages to
            // drain, so the queue never appears on the broker unless the reprocessor really has dead messages to
            // handle. Ensured on a throwaway channel (not the working one) so a passive-declare 404 can't take the
            // cycle's channel down with it.
            await EnsureParkingQueueAsync(connection, ct).ConfigureAwait(false);

            var toProcess = (int)Math.Min(initialCount, (uint)_maxMessagesPerCycle);

            int reenqueued = 0, exhausted = 0, malformed = 0, permanent = 0;

            for (int i = 0; i < toProcess && !ct.IsCancellationRequested; i++)
            {
                BasicGetResult? result;

                try
                {
                    result = await channel.BasicGetAsync(_deadLetterQueueName, autoAck: false, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[RABBIT-FLOW]: BasicGet failed on {Dlq}; aborting cycle.", _deadLetterQueueName);
                    break;
                }

                if (result == null) 
                    break;

                DeadLetterEnvelope? envelope = null;

                try
                {
                    envelope = JsonSerializer.Deserialize<DeadLetterEnvelope>(result.Body.Span, _serializerOptions);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[RABBIT-FLOW]: Failed to deserialize dead-letter envelope from {Dlq}.", _deadLetterQueueName);
                }

                if (envelope == null || !envelope.MessageData.HasValue)
                {
                    malformed++;

                    RabbitFlowDiagnostics.ParkedMessages.Add(1, new KeyValuePair<string, object?>("queue", _queueName), new KeyValuePair<string, object?>("reason", "malformed"));

                    await ParkUnchangedAsync(channel, result, ct).ConfigureAwait(false);

                    continue;
                }

                // Envelopes written by current versions carry the flag; older ones fall back to type-name matching
                var isTransient = envelope.IsTransient ?? TransientExceptionClassifier.IsTransientTypeName(envelope.ExceptionType);

                if (!isTransient)
                {
                    permanent++;
                    _logger.LogDebug("[RABBIT-FLOW]: Reprocessor for {Consumer} parking permanent failure ({ExceptionType}) from {Dlq}.",
                        _consumerName, envelope.ExceptionType ?? "(unknown)", _deadLetterQueueName);

                    RabbitFlowDiagnostics.ParkedMessages.Add(1, new KeyValuePair<string, object?>("queue", _queueName), new KeyValuePair<string, object?>("reason", "permanent"));

                    await ParkUnchangedAsync(channel, result, ct).ConfigureAwait(false);

                    continue;
                }

                if (envelope.ReprocessAttempts < _maxAttempts)
                {
                    var newAttempts = envelope.ReprocessAttempts + 1;

                    var payload = Encoding.UTF8.GetBytes(envelope.MessageData.Value.GetRawText());

                    var props = BuildRestoredProperties(envelope, newAttempts);

                    try
                    {
                        await channel.BasicPublishAsync(exchange: "", routingKey: _queueName, mandatory: false, basicProperties: props, body: payload, cancellationToken: ct).ConfigureAwait(false);

                        await channel.BasicAckAsync(result.DeliveryTag, false, ct).ConfigureAwait(false);

                        reenqueued++;

                        RabbitFlowDiagnostics.ReprocessedMessages.Add(1, new KeyValuePair<string, object?>("queue", _queueName));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[RABBIT-FLOW]: Failed to re-enqueue message to {Queue}; aborting cycle.", _queueName);
                        break;
                    }
                }
                else
                {
                    envelope.ReprocessAttempts = _maxAttempts;

                    try
                    {
                        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, _serializerOptions));

                        var parkProps = new BasicProperties
                        {
                            DeliveryMode = DeliveryModes.Persistent,
                            MessageId = envelope.MessageId,
                            CorrelationId = envelope.CorrelationId
                        };

                        await channel.BasicPublishAsync(exchange: "", routingKey: _parkingQueueName, mandatory: false, basicProperties: parkProps, body: bytes, cancellationToken: ct).ConfigureAwait(false);

                        await channel.BasicAckAsync(result.DeliveryTag, false, ct).ConfigureAwait(false);

                        exhausted++;

                        RabbitFlowDiagnostics.ParkedMessages.Add(1, new KeyValuePair<string, object?>("queue", _queueName), new KeyValuePair<string, object?>("reason", "exhausted"));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[RABBIT-FLOW]: Failed to park exhausted message to {Parking}.", _parkingQueueName);
                        break;
                    }
                }
            }

            _logger.LogInformation("[RABBIT-FLOW]: Reprocessor cycle for {Consumer} done. Reenqueued={Reenqueued}, Parked: Exhausted={Exhausted}, Permanent={Permanent}, Malformed={Malformed}.",
                _consumerName, reenqueued, exhausted, permanent, malformed);
        }

        // Make sure the parking queue exists without fighting over its arguments. We probe with a passive
        // declare (which never compares arguments): if the queue already exists we use it as-is, so an
        // operator's deliberate settings (TTL, queue type, max-length, ...) are respected and we never hit
        // PRECONDITION_FAILED. Only when it's missing (404) do we create it with defaults. The probe runs on a
        // throwaway channel because a 404 closes the channel it runs on.
        private async Task EnsureParkingQueueAsync(IConnection connection, CancellationToken ct)
        {
            try
            {
                using var probe = await connection.CreateChannelAsync(cancellationToken: ct).ConfigureAwait(false);

                await probe.QueueDeclarePassiveAsync(_parkingQueueName, ct).ConfigureAwait(false);

                if (!_parkingQueueExistsLogged)
                {
                    _parkingQueueExistsLogged = true;

                    _logger.LogInformation(
                        "[RABBIT-FLOW]: Parking queue '{Parking}' already exists; using it as-is. To apply different " +
                        "arguments (TTL, queue type, max-length, ...), delete the queue and the reprocessor will recreate it with defaults.",
                        _parkingQueueName);
                }

                return;
            }
            catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 404)
            {
                // Not found: fall through and create it on a fresh channel (the probe channel is now closed).
            }

            using var create = await connection.CreateChannelAsync(cancellationToken: ct).ConfigureAwait(false);

            await create.QueueDeclareAsync(_parkingQueueName, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct).ConfigureAwait(false);

            _logger.LogInformation("[RABBIT-FLOW]: Created parking queue '{Parking}'.", _parkingQueueName);
        }

        private static BasicProperties BuildRestoredProperties(DeadLetterEnvelope envelope, int newAttempts)
        {
            var props = new BasicProperties
            {
                MessageId = envelope.MessageId,
                CorrelationId = envelope.CorrelationId
            };

            var headers = new Dictionary<string, object?>();

            var original = envelope.Properties;

            if (original != null)
            {
                if (original.DeliveryMode.HasValue)
                    props.DeliveryMode = (DeliveryModes)(byte)original.DeliveryMode.Value;

                if (original.Type != null)
                    props.Type = original.Type;

                if (original.AppId != null)
                    props.AppId = original.AppId;

                if (original.Priority.HasValue)
                    props.Priority = original.Priority.Value;

                if (original.ContentType != null)
                    props.ContentType = original.ContentType;

                if (original.ReplyTo != null)
                    props.ReplyTo = original.ReplyTo;

                if (original.Headers != null)
                {
                    foreach (var kvp in original.Headers)
                    {
                        headers[kvp.Key] = kvp.Value;
                    }
                }
            }

            headers[RabbitFlowHeaders.ReprocessAttempts] = newAttempts;

            props.Headers = headers;

            return props;
        }

        private async Task ParkUnchangedAsync(IChannel channel, BasicGetResult result, CancellationToken ct)
        {
            try
            {
                // Preserve the original message's properties (MessageId, CorrelationId, headers, ...) and force
                // persistence so parked messages survive a broker restart on the durable parking queue.
                var parkProps = result.BasicProperties != null
                    ? new BasicProperties(result.BasicProperties) { DeliveryMode = DeliveryModes.Persistent }
                    : new BasicProperties { DeliveryMode = DeliveryModes.Persistent };

                await channel.BasicPublishAsync(exchange: "", routingKey: _parkingQueueName, mandatory: false, basicProperties: parkProps, body: result.Body, cancellationToken: ct).ConfigureAwait(false);
                await channel.BasicAckAsync(result.DeliveryTag, false, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RABBIT-FLOW]: Failed to park message to {Parking}.", _parkingQueueName);
            }
        }
    }
}
