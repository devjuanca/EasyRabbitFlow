using EasyRabbitFlow.Settings;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace EasyRabbitFlow.Services
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
        /// <param name="publisherId"/> Sets an identifier to the created connection.
        /// <param name="jsonOptions">If set JsonSerializerOptions will be use over the one configured by default</param>
        /// <returns>Boolean indicating if the message was published</returns>
        Task<bool> PublishAsync<TEvent>(TEvent message, string exchangeName, string routingKey, string publisherId = "", JsonSerializerOptions? jsonOptions = null) where TEvent : class;

        /// <summary>
        /// Publishes a message to a RabbitMQ queue asynchronously.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="message"></param>
        /// <param name="queueName"></param>
        /// <param name="publisherId"/> Sets an identifier to the created connection.
        /// <param name="jsonOptions">If set JsonSerializerOptions will be use over the one configured by default</param>
        /// <returns>Boolean indicating if the message was published</returns>
        Task<bool> PublishAsync<TEvent>(TEvent message, string queueName, string publisherId = "", JsonSerializerOptions? jsonOptions = null) where TEvent : class;
    }

    internal class RabbitFlowPublisher : IRabbitFlowPublisher
    {
        private readonly ConnectionFactory connectionFactory;

        private IConnection? globalConnection = null;

        private readonly JsonSerializerOptions jsonOptions;

        private readonly PublisherOptions publisherOptions;

        private readonly ILogger<RabbitFlowPublisher> logger;

        private readonly object lockObject = new object();

        public RabbitFlowPublisher(ConnectionFactory connectionFactory, ILogger<RabbitFlowPublisher> logger, PublisherOptions? publisherOptions = null, JsonSerializerOptions? jsonOptions = null)
        {
            this.logger = logger;

            this.connectionFactory = connectionFactory;

            this.jsonOptions = jsonOptions ?? new JsonSerializerOptions();

            this.publisherOptions = publisherOptions ?? new PublisherOptions();
        }

        public Task<bool> PublishAsync<TEvent>(TEvent @event, string exchangeName, string routingKey = "", string publisherId = "", JsonSerializerOptions? jsonSerializerOptions = null) where TEvent : class
        {
            var serializerOptions = jsonSerializerOptions ?? jsonOptions;

            try
            {
                if (@event is null)
                {
                    throw new ArgumentNullException(nameof(@event));
                }

                if (string.IsNullOrEmpty(publisherId))
                {
                    publisherId = string.Concat(exchangeName, "_", routingKey);
                }

                var connection = ResolveConnection(publisherId);

                using (var channel = connection.CreateModel())
                {
                    ReadOnlyMemory<byte> body = JsonSerializer.SerializeToUtf8Bytes(@event, serializerOptions);

                    var props = channel.CreateBasicProperties();

                    channel.BasicPublish(exchangeName, routingKey, props, body);
                }

                logger.LogInformation("Message of type: {messageType} was published.", typeof(TEvent).FullName);

                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[RABBIT-FLOW]: Error publishing a message");

                throw;
            }
            finally
            {

                if (publisherOptions.DisposePublisherConnection && globalConnection != null)
                {
                    DisposeGlobalConnection();
                }

            }
        }

        public Task<bool> PublishAsync<TEvent>(TEvent @event, string queueName, string publisherId = "", JsonSerializerOptions? jsonSerializerOptions = null) where TEvent : class
        {

            var serializerOptions = jsonSerializerOptions ?? jsonOptions;

            try
            {
                if (@event is null)
                {
                    throw new ArgumentNullException(nameof(@event));
                }

                if (string.IsNullOrEmpty(publisherId))
                {
                    publisherId = queueName;
                }

                var connection = ResolveConnection(publisherId);

                using (var channel = connection.CreateModel())
                {
                    ReadOnlyMemory<byte> body = JsonSerializer.SerializeToUtf8Bytes(@event, serializerOptions);

                    var props = channel.CreateBasicProperties();

                    channel.BasicPublish("", queueName, props, body); // Empty string for exchange name
                }

                logger.LogInformation("Message of type: {messageType} was published.", typeof(TEvent).FullName);

                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[RABBIT-FLOW]: Error publishing a message");

                throw;
            }
            finally
            {
                if (publisherOptions.DisposePublisherConnection && globalConnection != null)
                {
                    DisposeGlobalConnection();
                }
            }
        }

        private IConnection ResolveConnection(string connectionId)
        {
            if (globalConnection == null)
            {
                lock (lockObject)
                {
                    //double-checked locking pattern
                    globalConnection ??= connectionFactory.CreateConnection($"Publisher_{connectionId}");
                }

                return globalConnection;
            }
            else
            {
                return globalConnection;
            }
        }

        private void DisposeGlobalConnection()
        {
            lock (lockObject)
            {
                globalConnection?.Close();

                globalConnection?.Dispose();

                globalConnection = null;
            }
        }
    }
}