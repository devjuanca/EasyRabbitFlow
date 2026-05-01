using EasyRabbitFlow.Exceptions;
using EasyRabbitFlow.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
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
    /// configured with <see cref="DeadLetterReprocessSettings{TConsumer}"/> and re-publishes messages
    /// back to the main queue until the configured maximum attempt count is reached.
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
        private readonly int _maxAttempts;
        private readonly TimeSpan _interval;
        private readonly int _maxMessagesPerCycle;

        private Task? _runTask;

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
            _deadLetterQueueName = $"{queueName}-deadletter";
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

        private async Task RunCycleAsync(CancellationToken ct)
        {
            using var connection = await _connectionFactory.CreateConnectionAsync($"reprocessor_{_queueName}", ct).ConfigureAwait(false);
            
            using var channel = await connection.CreateChannelAsync(cancellationToken: ct).ConfigureAwait(false);

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
                    
                    await RepublishUnchangedAsync(channel, result, ct).ConfigureAwait(false);
                    
                    continue;
                }

                if (!IsTransientException(envelope.ExceptionType))
                {
                    permanent++;
                    _logger.LogDebug("[RABBIT-FLOW]: Reprocessor for {Consumer} skipping permanent failure ({ExceptionType}) in {Dlq}.",
                        _consumerName, envelope.ExceptionType ?? "(unknown)", _deadLetterQueueName);
                    
                    await RepublishUnchangedAsync(channel, result, ct).ConfigureAwait(false);
                    
                    continue;
                }

                if (envelope.ReprocessAttempts < _maxAttempts)
                {
                    var newAttempts = envelope.ReprocessAttempts + 1;

                    var payload = Encoding.UTF8.GetBytes(envelope.MessageData.Value.GetRawText());

                    var props = new BasicProperties
                    {
                        MessageId = envelope.MessageId,
                        CorrelationId = envelope.CorrelationId,
                        Headers = new Dictionary<string, object?>
                        {
                            [RabbitFlowHeaders.ReprocessAttempts] = newAttempts
                        }
                    };

                    try
                    {
                        await channel.BasicPublishAsync(exchange: "", routingKey: _queueName, mandatory: false, basicProperties: props, body: payload, cancellationToken: ct).ConfigureAwait(false);
                        
                        await channel.BasicAckAsync(result.DeliveryTag, false, ct).ConfigureAwait(false);
                        
                        reenqueued++;
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
                        
                        await channel.BasicPublishAsync(exchange: "", routingKey: _deadLetterQueueName, body: bytes, cancellationToken: ct).ConfigureAwait(false);
                        
                        await channel.BasicAckAsync(result.DeliveryTag, false, ct).ConfigureAwait(false);
                        
                        exhausted++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[RABBIT-FLOW]: Failed to re-publish exhausted message to {Dlq}.", _deadLetterQueueName);
                        break;
                    }
                }
            }

            _logger.LogInformation("[RABBIT-FLOW]: Reprocessor cycle for {Consumer} done. Reenqueued={Reenqueued}, Exhausted={Exhausted}, Permanent={Permanent}, Malformed={Malformed}.",
                _consumerName, reenqueued, exhausted, permanent, malformed);
        }

        private static bool IsTransientException(string? exceptionType)
        {
            if (string.IsNullOrEmpty(exceptionType)) return false;

            return exceptionType == nameof(OperationCanceledException)
                || exceptionType == nameof(TaskCanceledException)
                || exceptionType == nameof(RabbitFlowTransientException);
        }

        private async Task RepublishUnchangedAsync(IChannel channel, BasicGetResult result, CancellationToken ct)
        {
            try
            {
                await channel.BasicPublishAsync(exchange: "", routingKey: _deadLetterQueueName, body: result.Body, cancellationToken: ct).ConfigureAwait(false);
                await channel.BasicAckAsync(result.DeliveryTag, false, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RABBIT-FLOW]: Failed to re-publish malformed message to {Dlq}.", _deadLetterQueueName);
            }
        }
    }
}
