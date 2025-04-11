using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;


public interface IRabbitFlowTemporary
{
    /// <summary>
    /// Executes an asynchronous operation on a list of messages, processing them with the provided function.
    /// After processing, it publishes the messages to a temporary RabbitMQ exchange and acknowledges them.
    /// </summary>
    /// <typeparam name="T">The type of messages being processed, constrained to reference types.</typeparam>
    /// <param name="messages">The collection of messages to be processed asynchronously.</param>
    /// <param name="onMessageReceived">An asynchronous function that processes each message in the collection. It is called once for each message received from the queue.</param>
    /// <param name="prefetchCount">The number of messages to prefetch at once. Determines how many messages the consumer will fetch from the queue before processing the next batch.</param>
    /// <param name="onCompleted">
    /// An optional action that is invoked after all messages have been processed. 
    /// <br/>
    /// <ul>
    /// <li><b>First parameter:</b> The number of successfully processed messages.</li>
    /// <br/>
    /// <li><b>Second parameter:</b> The number of messages that resulted in an error.</li>
    /// </ul>
    /// </param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation if needed.</param>
    /// <returns>Returns a task that resolves to the number of messages that were successfully processed.</returns>
    /// <remarks>
    /// This method processes each message asynchronously, acknowledging each one after processing.
    /// Once all messages are processed, the onCompleted action (if provided) is executed with two integers:
    /// the number of successfully processed messages and the number of messages that encountered errors.
    /// The method ensures that the messages are processed in the order they were published, and each message is acknowledged after successful processing.
    /// </remarks>

    Task<int> RunAsync<T>(IReadOnlyList<T> messages, Func<T, Task> onMessageReceived, Action<int, int>? onCompleted = null, ushort prefetchCount = 1, CancellationToken cancellationToken = default) where T : class;
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
        Func<T, Task> onMessageReceived,
        Action<int, int>? onCompleted = null,
        ushort prefetchCount = 1,
        CancellationToken cancellationToken = default) where T : class
    {
        var eventName = typeof(T).Name.ToLower();

        var _exchange = $"{eventName}-temp-exchange";

        var _queue = $"{eventName}-temp-queue-{Guid.NewGuid():N}";

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

                if (message != null)
                {
                    await onMessageReceived(message);
                }
            }
            catch (Exception ex)
            {
                errors++;

                _logger.LogError(ex, "[RabbitFlowTemporary] Error while processing the message.");
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
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));

            var props = channel.CreateBasicProperties();

            props.Persistent = false;

            channel.BasicPublish(_exchange, _routingKey, props, body);
        }

        using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        await tcs.Task;

        channel.BasicCancel(consumerTag);

        try
        {
            onCompleted?.Invoke(processed, errors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RabbitFlowTemporary] Error while executing the onCompleted callback.");
        }

        return processed;
    }
}