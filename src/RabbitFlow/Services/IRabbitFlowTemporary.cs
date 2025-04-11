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
    Task RunAsync<T>(IReadOnlyList<T> messagesToPublish, Func<T, Task> onMessage, CancellationToken cancellationToken) where T : class;
}

public class RabbitFlowTemporary : IRabbitFlowTemporary
{
    private readonly IConnection _connection;

    private readonly ILogger<RabbitFlowTemporary> _logger;

    public RabbitFlowTemporary(ConnectionFactory connectionFactory, ILogger<RabbitFlowTemporary> logger)
    {
        _connection = connectionFactory.CreateConnection();

        _logger = logger;
    }

    public async Task RunAsync<T>(IReadOnlyList<T> messagesToPublish, Func<T, Task> onMessage, CancellationToken cancellationToken) where T : class
    {
        using var channel = _connection.CreateModel();

        var eventName = typeof(T).Name.ToLower();

        var _exchange = $"{eventName}-temporary-exchange";

        var _queue = $"{eventName}-temporary-queue-{Guid.NewGuid():N}";

        var _routingKey = $"{eventName}-temporary-routing-key";

        var maxMessages = messagesToPublish.Count;

        channel.ExchangeDeclare(_exchange, ExchangeType.Direct, durable: false, autoDelete: true);

        channel.QueueDeclare(_queue, durable: false, exclusive: true, autoDelete: true);

        channel.QueueBind(_queue, _exchange, _routingKey);

        var processed = 0;

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
                    await onMessage(message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TempChannel] Error procesando mensaje.");
            }

            processed++;

            if (maxMessages > 0 && processed >= maxMessages)
            {
                tcs.TrySetResult(true);
            }
        };

        var consumerTag = channel.BasicConsume(_queue, autoAck: true, consumer);

        foreach (var msg in messagesToPublish)
        {
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));
            var props = channel.CreateBasicProperties();
            props.Persistent = false;

            channel.BasicPublish(_exchange, _routingKey, props, body);
        }

        using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        await tcs.Task;

        channel.BasicCancel(consumerTag);
    }
}
