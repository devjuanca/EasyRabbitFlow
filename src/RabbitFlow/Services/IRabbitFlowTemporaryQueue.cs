using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RabbitFlow.Settings;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace RabbitFlow.Services
{
    /// <summary>
    /// Represents a service for managing temporary RabbitMQ queues and handling message publishing and consumption.
    /// </summary>
    public interface IRabbitFlowTemporaryQueue : IDisposable
    {
        /// <summary>
        /// Publishes an event to a specified queue asynchronously.
        /// </summary>
        /// <typeparam name="TEvent">The type of the event.</typeparam>
        /// <param name="queueName">The name of the queue to publish to.</param>
        /// <param name="event">The event to publish.</param>
        /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
        /// <returns>A task representing the asynchronous publishing operation.</returns>
        Task PublishAsync<TEvent>(string queueName, TEvent @event, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sets up a temporary consumer handler for the specified queue, allowing message consumption.
        /// </summary>
        /// <typeparam name="TEvent">The type of the event.</typeparam>
        /// <param name="queueName">The name of the queue to consume from.</param>
        /// <param name="settings">Temporary consumer settings.</param>
        /// <param name="temporaryConsumerHandler">The handler to process received events.</param>
        void SetTemporaryConsumerHandler<TEvent>(string queueName, TemporaryConsummerSettings settings = default!, Func<TEvent, CancellationToken, Task> temporaryConsumerHandler = default!);
    }

    /// <summary>
    /// Provides functionality for managing temporary RabbitMQ queues and handling message publishing and consumption.
    /// </summary>
    internal class RabbitFlowTemporaryQueue : IRabbitFlowTemporaryQueue
    {
        private readonly ConnectionFactory _connectionFactory;
        private IConnection _connection;
        private readonly JsonSerializerOptions _jsonOptions;
        private IModel? _channel;
        private Timer _cleanupTimer = default!;

        public RabbitFlowTemporaryQueue(ConnectionFactory connectionFactory, JsonSerializerOptions? jsonOptions = null)
        {
            _connectionFactory = connectionFactory;
            _jsonOptions = jsonOptions ?? new JsonSerializerOptions();
            _connection = _connectionFactory.CreateConnection($"TempQueue-{Guid.NewGuid()}");

        }


        public Task PublishAsync<TEvent>(string queueName, TEvent @event, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            if (_channel != null)
            {
                var body = JsonSerializer.SerializeToUtf8Bytes(@event, _jsonOptions);

                _channel.BasicPublish(exchange: "", routingKey: queueName, basicProperties: null, body: body);
            }

            return Task.CompletedTask;
        }

        public void SetTemporaryConsumerHandler<TEvent>(string queueName, TemporaryConsummerSettings settings = default!, Func<TEvent, CancellationToken, Task> temporaryConsumerHandler = default!)
        {
            Dispose();

            _cleanupTimer = new Timer(CheckQueueStatus, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));

            _channel = DeclareTemporaryQueue(queueName, settings);

            if (_channel != null)
            {
                var consumer = new EventingBasicConsumer(_channel);

                consumer.Received += async (model, ea) =>
                {
                    using var cancellationSource = new CancellationTokenSource(_channel.ContinuationTimeout);

                    var cancellationToken = cancellationSource.Token;

                    var receivedMessage = Encoding.UTF8.GetString(ea.Body.ToArray());

                    var receivedEvent = JsonSerializer.Deserialize<TEvent>(receivedMessage, _jsonOptions);

                    if (receivedEvent != null)
                    {
                        await temporaryConsumerHandler.Invoke(receivedEvent, cancellationSource.Token);
                    }

                };

                if (_channel?.ConsumerCount(queueName) == 0)
                    _channel.BasicConsume(queue: queueName, autoAck: true, consumer: consumer, consumerTag: queueName);
            }
        }

        public void Dispose()
        {
            StopConsumingAll(false);
            DisposeResources();
            _cleanupTimer?.Dispose();
        }


        //Private methods
        private IModel? DeclareTemporaryQueue(string queueName, TemporaryConsummerSettings settings)
        {
            try
            {
                _connection = _connectionFactory.CreateConnection($"TemporaryConnection-{Guid.NewGuid()}");

                var channel = _connection.CreateModel();

                channel.ContinuationTimeout = settings.Timeout;

                channel.BasicQos(prefetchSize: 0, prefetchCount: settings.PrefetchCount, global: false);

                channel.QueueDeclare(queue: queueName, durable: false, exclusive: false, autoDelete: true, arguments: null);

                return channel;
            }
            catch
            {
                return null;
            }
        }

        private void StopConsumingAll(bool alreadyDeletedQueue)
        {
            if (_channel != null)
            {
                if (!alreadyDeletedQueue)
                {
                    var queueName = _channel.CurrentQueue;

                    if (queueName != null)
                    {
                        try
                        {
                            _channel.BasicCancel(queueName);
                        }
                        catch (AlreadyClosedException)
                        {
                            //Do nothing
                        }

                    }

                    _channel.Dispose();

                    _channel = null;
                }

            }
        }

        private void DisposeResources()
        {
            _connection?.Dispose();
        }

        private void CheckQueueStatus(object state)
        {
            try
            {
                if (_channel != null)
                {
                    var queueName = _channel.CurrentQueue;
                    var queueStats = _channel.QueueDeclarePassive(queueName);
                    var messageCount = queueStats.MessageCount;

                    if (messageCount == 0)
                    {
                        DisposeResources();
                    }
                }
            }
            catch (AlreadyClosedException)
            {
                DisposeResources();
                StopConsumingAll(true);
                _cleanupTimer?.Dispose();
            }
        }
    }
}