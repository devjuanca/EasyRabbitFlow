using RabbitFlow.Settings;
using RabbitMQ.Client;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace RabbitFlow.Services
{
    /// <summary>
    /// Represents a publisher for sending RabbitMQ messages.
    /// </summary>
    public interface IRabbitFlowPublisher
    {
        /// <summary>
        /// Publishes a message to a RabbitMQ exchange asynchronously.
        /// </summary>
        /// <typeparam name="TEvent">The type of the message to publish.</typeparam>
        /// <param name="message">The message to publish.</param>
        /// <param name="exchangeName">The name of the exchange to publish the message to.</param>
        /// <param name="routingKey">The routing key for the message.</param>
        /// <param name="useNewConnection">Indicates whether to use a new connection for publishing.</param>
        /// <returns>A task representing the asynchronous publishing process.</returns>
        /// <remarks>
        /// Implement this method to define the logic for publishing a message to a RabbitMQ exchange.
        /// The <paramref name="exchangeName"/> specifies the exchange to publish the message to.
        /// The <paramref name="routingKey"/> is used to route the message within the exchange.
        /// The <paramref name="useNewConnection"/> parameter controls whether to use a new connection for publishing.
        /// </remarks>
        Task PublishAsync<TEvent>(TEvent message, string exchangeName, string routingKey, bool useNewConnection = false);

        /// <summary>
        /// Publishes a message to a RabbitMQ queue asynchronously.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <param name="queueName"></param>
        /// <param name="useNewConnection"></param>
        /// <returns></returns>
        Task PublishAsync<TEvent>(TEvent @event, string queueName, bool useNewConnection = false);
    }

    internal class RabbitFlowPublisher : IRabbitFlowPublisher
    {
        private readonly ConnectionFactory _connectionFactory;

        private readonly IConnection _connection;

        private readonly JsonSerializerOptions _jsonOptions;

        private readonly PublisherOptions _publisherOptions;

        public string Name { get; set; } = string.Empty;

        public RabbitFlowPublisher(ConnectionFactory connectionFactory, PublisherOptions? publisherOptions = null, JsonSerializerOptions? jsonOptions = null)
        {
            _connectionFactory = connectionFactory;
            _jsonOptions = jsonOptions ?? new JsonSerializerOptions();
            _connection = _connectionFactory.CreateConnection($"Publisher-{Guid.NewGuid()}");
            _publisherOptions = publisherOptions ?? new PublisherOptions();
        }

        public async Task PublishAsync<TEvent>(TEvent @event, string exchangeName, string routingKey, bool useNewConnection = false)
        {
            try
            {
                IConnection connection;

                if (useNewConnection)
                {
                    connection = _connectionFactory.CreateConnection($"Publisher-{Guid.NewGuid()}");
                }
                else
                {
                    if (_connection == null || !_connection.IsOpen)
                        connection = _connectionFactory.CreateConnection($"Publisher-{Guid.NewGuid()}");
                    else

                        connection = _connection;
                }

                using var channel = connection.CreateModel();

                var body = JsonSerializer.SerializeToUtf8Bytes(@event, _jsonOptions);

                var props = channel.CreateBasicProperties();

                channel.BasicPublish(exchangeName, routingKey, props, body);

                if (useNewConnection || (_publisherOptions.DisposePublisherConnection && connection != null && connection.IsOpen))
                {
                    connection.Close();
                    connection.Dispose();
                }
                if (_publisherOptions.DisposePublisherConnection && _connection != null && _connection.IsOpen)
                {
                    _connection.Close();
                    _connection.Dispose();
                }

                await Task.CompletedTask;
            }
            catch
            {
                throw;
            }
        }

        public async Task PublishAsync<TEvent>(TEvent @event, string queueName, bool useNewConnection = false)
        {
            try
            {
                IConnection connection;

                if (useNewConnection)
                {
                    connection = _connectionFactory.CreateConnection($"Publisher-{Guid.NewGuid()}");
                }
                else
                {
                    if (_connection == null || !_connection.IsOpen)
                        connection = _connectionFactory.CreateConnection($"Publisher-{Guid.NewGuid()}");
                    else
                        connection = _connection;
                }

                using var channel = connection.CreateModel();

                var body = JsonSerializer.SerializeToUtf8Bytes(@event, _jsonOptions);

                var props = channel.CreateBasicProperties();

                channel.BasicPublish("", queueName, props, body); // Empty string for exchange name

                if (useNewConnection || (_publisherOptions.DisposePublisherConnection && connection != null && connection.IsOpen))
                {
                    connection.Close();
                    connection.Dispose();
                }
                if (_publisherOptions.DisposePublisherConnection && _connection != null && _connection.IsOpen)
                {
                    _connection.Close();
                    _connection.Dispose();
                }

                await Task.CompletedTask;
            }
            catch
            {
                throw;
            }
        }

    }
}