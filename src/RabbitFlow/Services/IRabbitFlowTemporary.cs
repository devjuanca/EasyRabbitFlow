using EasyRabbitFlow.Settings;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ExchangeType = RabbitMQ.Client.ExchangeType;


public interface IRabbitFlowTemporary
{
    /// <summary>
    /// Publishes a collection of messages to a temporary RabbitMQ exchange and consumes them asynchronously.
    /// Each message is processed using the provided handler function, with support for cancellation and per-message timeout.
    /// An optional completion callback can be invoked once all messages have been processed.
    /// </summary>
    /// <typeparam name="T">The type of messages to process. Must be a reference type.</typeparam>
    /// <param name="messages">The collection of messages to publish and process.</param>
    /// <param name="onMessageReceived">
    /// An asynchronous function invoked for each received message.
    /// Supports cancellation via the provided <see cref="CancellationToken"/>.
    /// </param>
    /// <param name="onCompleted">
    /// An optional callback executed after all messages have been processed.
    /// <br/>
    /// <ul>
    /// <li><b>First parameter:</b> The number of successfully processed messages.</li>
    /// <li><b>Second parameter:</b> The number of messages that encountered errors.</li>
    /// </ul>
    /// </param>
    /// <param name="options">Optional configuration settings for the temporary queue behavior.</param>
    /// <param name="cancellationToken">
    /// A token that can be used to cancel the overall operation early. This will cancel the consumption process and stop waiting for message completion.
    /// <br/>
    /// <b>When triggered, it will:</b>
    /// <ul>
    ///   <li>Abort waiting for all messages to be processed.</li>
    ///   <li>Cancel any in-progress message handlers.</li>
    ///   <li>Stop consuming further messages from the temporary queue.</li>
    /// </ul>
    /// This token is typically used to propagate shutdown signals or client-side timeouts.
    /// <br/><br/>
    /// <b>If no cancellation token is provided:</b>
    /// <ul>
    ///   <li>The process will continue running until all messages are processed from the queue.</li>
    ///   <li>A per-message timeout (if configured via <see cref="RunTemporaryOptions.Timeout"/>) will still apply using an internal cancellation token for each message handler.</li>
    /// </ul>
    /// </param>

    /// <returns>The number of successfully processed messages.</returns>

    Task<int> RunAsync<T>(IReadOnlyList<T> messages, Func<T, CancellationToken, Task> onMessageReceived, Action<int, int>? onCompleted = null, RunTemporaryOptions? options = null, CancellationToken cancellationToken = default) where T : class;

}

public class RabbitFlowTemporary : IRabbitFlowTemporary
{
    private readonly ConnectionFactory _connectionFactory;

    private readonly ILogger<RabbitFlowTemporary> _logger;

    public RabbitFlowTemporary(ConnectionFactory connectionFactory, ILogger<RabbitFlowTemporary> logger)
    {
        _connectionFactory = connectionFactory;

        _logger = logger;
    }

    public async Task<int> RunAsync<T>(
        IReadOnlyList<T> messages,
        Func<T, CancellationToken, Task> onMessageReceived,
        Action<int, int>? onCompleted = null,
        RunTemporaryOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        if (messages is null || messages.Count == 0)
        {
            return 0;
        }

        options ??= new RunTemporaryOptions();

        var correlationId = options.CorrelationId;

        var prefetchCount = options.Prefetch;

        var timeout = options.Timeout;

        var queuePrefixName = options.QueuePrefixName;

        var eventName = typeof(T).Name.ToLower();

        var _exchange = $"{eventName}-temp-exchange";

        var _queue = $"{queuePrefixName ?? eventName}-temp-queue-{Guid.NewGuid():N}";

        var _routingKey = $"{eventName}-temp-routing-key";

        using var connection = _connectionFactory.CreateConnection($"{_queue}");

        using var channel = connection.CreateModel();

        var maxMessages = messages.Count;

        channel.ExchangeDeclare(_exchange, ExchangeType.Direct, durable: false, autoDelete: true);

        channel.QueueDeclare(_queue, durable: false, exclusive: true, autoDelete: true);

        channel.QueueBind(_queue, _exchange, _routingKey);

        channel.BasicQos(prefetchSize: 0, prefetchCount: prefetchCount, global: false);

        var processed = 0;

        var errors = 0;

        var tcs = new TaskCompletionSource<bool>();

        var consumer = new EventingBasicConsumer(channel);

        consumer.Received += async (model, ea) =>
        {
            try
            {
                var json = Encoding.UTF8.GetString(ea.Body.Span);

                var message = JsonSerializer.Deserialize<T>(json);

                if (message is null)
                {
                    _logger.LogWarning("[RabbitFlowTemporary] Message is null. CorrelationId: {correlationId}", correlationId);

                    Interlocked.Increment(ref errors);

                    return;
                }

                using var timeoutCts = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : null;

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts?.Token ?? CancellationToken.None);

                try
                {
                    await onMessageReceived(message, linkedCts.Token).ConfigureAwait(false);
                }
                catch (TaskCanceledException) when (timeoutCts != null && timeoutCts.Token.IsCancellationRequested)
                {
                    Interlocked.Increment(ref errors);

                    _logger.LogError("[RabbitFlowTemporary] Message processing timed out after {Timeout}.  CorrelationId: {correlationId}", timeout, correlationId);
                }
                catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref errors);

                    _logger.LogError("[RabbitFlowTemporary] Message processing was canceled by main Cancellation Token. CorrelationId: {correlationId}", correlationId);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref errors);
                    _logger.LogError("[RabbitFlowTemporary] Message processing was canceled. CorrelationId: {correlationId}", correlationId);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref errors);

                    _logger.LogError(ex, "[RabbitFlowTemporary] Error while processing the message.  CorrelationId: {correlationId}", correlationId);
                }

            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref errors);

                _logger.LogError(ex, "[RabbitFlowTemporary] Error while processing the message. CorrelationId: {correlationId}", correlationId);
            }
            finally
            {
                if (channel.IsOpen)
                {
                    channel.BasicAck(deliveryTag: ea.DeliveryTag, multiple: false);
                }
            }

            if (Interlocked.Increment(ref processed) >= maxMessages)
            {
                tcs.TrySetResult(true);
            }
        };

        var consumerTag = channel.BasicConsume(_queue, autoAck: false, consumer);

        foreach (var msg in messages)
        {
            try
            {
                var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));

                var props = channel.CreateBasicProperties();

                props.Persistent = false;

                channel.BasicPublish(_exchange, _routingKey, props, body);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref errors);

                _logger.LogError(ex, "[RabbitFlowTemporary] Error publishing message to exchange. CorrelationId: {correlationId}", correlationId);
            }
        }

        using var _ = cancellationToken.Register(() =>
        {
            tcs.TrySetCanceled(cancellationToken);
        });

        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            _logger.LogError("[RabbitFlowTemporary] Task was canceled by main Cancellation Token. CorrelationId: {correlationId}", correlationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RabbitFlowTemporary] Error while waiting for messages to be processed. CorrelationId: {correlationId}", correlationId);
        }
        finally
        {
            channel.BasicCancel(consumerTag);
        }

        try
        {
            onCompleted?.Invoke(processed, errors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RabbitFlowTemporary:{CorrelationId}] Error while processing the message.", correlationId);
        }

        return processed;
    }

}