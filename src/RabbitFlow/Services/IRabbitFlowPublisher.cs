using EasyRabbitFlow.Settings;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace EasyRabbitFlow.Services
{

    /// <summary>
    /// Defines methods for publishing messages to RabbitMQ exchanges or queues.
    /// </summary>
    public interface IRabbitFlowPublisher
    {
        /// <summary>
        /// Asynchronously publishes a message to a RabbitMQ exchange.
        /// </summary>
        /// <typeparam name="TEvent">The type of the message to publish.</typeparam>
        /// <param name="message">The message to publish.</param>
        /// <param name="exchangeName">The name of the exchange to publish the message to.</param>
        /// <param name="routingKey">The routing key for the message.</param>
        /// <param name="publisherId">An optional identifier for the publisher connection.</param>
        /// <param name="jsonOptions">Optional JSON serializer options.</param>
        /// <param name="confirmModeTimeSpan">Optional timeout duration for waiting for message confirms in confirm mode.</param>
        /// <returns>A task representing the asynchronous operation. The task result is <c>true</c> if the message was published successfully; otherwise, <c>false</c>.</returns>
        Task<bool> PublishAsync<TEvent>(TEvent message, string exchangeName, string routingKey, string publisherId = "", JsonSerializerOptions? jsonOptions = null, TimeSpan? confirmModeTimeSpan = null) where TEvent : class;


        /// <summary>
        /// Asynchronously publishes a message to a RabbitMQ queue.
        /// </summary>
        /// <typeparam name="TEvent">The type of the message to publish.</typeparam>
        /// <param name="message">The message to publish.</param>
        /// <param name="queueName">The name of the queue to publish the message to.</param>
        /// <param name="publisherId">An optional identifier for the publisher connection.</param>
        /// <param name="jsonOptions">Optional JSON serializer options.</param>
        /// <param name="confirmModeTimeSpan">Optional timeout duration for waiting for message confirms in confirm mode.</param>
        /// <returns>A task representing the asynchronous operation. The task result is <c>true</c> if the message was published successfully; otherwise, <c>false</c>.</returns>
        Task<bool> PublishAsync<TEvent>(TEvent message, string queueName, string publisherId = "", JsonSerializerOptions? jsonOptions = null, TimeSpan? confirmModeTimeSpan = null) where TEvent : class;
    }

    internal class RabbitFlowPublisher : IRabbitFlowPublisher
    {
        private readonly ConnectionFactory connectionFactory;
        private readonly JsonSerializerOptions jsonOptions;
        private readonly PublisherOptions publisherOptions;
        private readonly ILogger<RabbitFlowPublisher> logger;
        private IConnection? globalConnection;
        private readonly object lockObject = new object();

        public RabbitFlowPublisher(ConnectionFactory connectionFactory, ILogger<RabbitFlowPublisher> logger, PublisherOptions? publisherOptions = null, JsonSerializerOptions? jsonOptions = null)
        {
            this.connectionFactory = connectionFactory;
            this.logger = logger;
            this.jsonOptions = jsonOptions ?? new JsonSerializerOptions();
            this.publisherOptions = publisherOptions ?? new PublisherOptions();
        }

        public async Task<bool> PublishAsync<TEvent>(TEvent @event, string exchangeName, string routingKey = "", string publisherId = "", JsonSerializerOptions? jsonSerializerOptions = null, TimeSpan? confirmModeTimeSpan = null) where TEvent : class
        {
            return await PublishMessageAsync(@event, exchangeName, routingKey, publisherId, jsonSerializerOptions, isQueue: false, confirmModeTimeSpan);
        }

        public async Task<bool> PublishAsync<TEvent>(TEvent @event, string queueName, string publisherId = "", JsonSerializerOptions? jsonSerializerOptions = null, TimeSpan? confirmModeTimeSpan = null) where TEvent : class
        {
            return await PublishMessageAsync(@event, queueName, "", publisherId, jsonSerializerOptions, isQueue: true, confirmModeTimeSpan);
        }

        private Task<bool> PublishMessageAsync<TEvent>(TEvent @event, string destination, string routingKey, string publisherId, JsonSerializerOptions? jsonSerializerOptions, bool isQueue, TimeSpan? confirmModeTimeSpan = null) where TEvent : class
        {
            if (@event is null)
            {
                throw new ArgumentNullException(nameof(@event));
            }

            publisherId ??= isQueue ? destination : string.Concat(destination, "_", routingKey);

            var connection = ResolveConnection(publisherId);

            var serializerOptions = jsonSerializerOptions ?? jsonOptions;

            using var channel = connection.CreateModel();

            try
            {
                if (publisherOptions.ChannelMode == ChannelMode.Transactional)
                {
                    channel.TxSelect();

                    Publish(channel, @event, destination, routingKey, serializerOptions, isQueue);

                    channel.TxCommit();
                }
                else
                {
                    channel.ConfirmSelect();

                    Publish(channel, @event, destination, routingKey, serializerOptions, isQueue);

                    confirmModeTimeSpan ??= TimeSpan.FromSeconds(5);

                    bool result = channel.WaitForConfirms(confirmModeTimeSpan.Value, out bool timedOut);

                    if (timedOut)
                    {
                        return Task.FromResult(false);
                    }
                }

                logger.LogInformation("Message of type: {messageType} was published.", typeof(TEvent).FullName);

                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                if (publisherOptions.ChannelMode == ChannelMode.Transactional)
                {
                    channel.TxRollback();
                }

                logger.LogError(ex, "[RABBIT-FLOW]: Error publishing a message");

                return Task.FromResult(false);
            }
            finally
            {
                if (publisherOptions.DisposePublisherConnection && globalConnection != null)
                {
                    DisposeGlobalConnection();
                }
            }
        }

        private void Publish<TEvent>(IModel channel, TEvent @event, string destination, string routingKey, JsonSerializerOptions serializerOptions, bool isQueue) where TEvent : class
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(@event, serializerOptions);

            var props = channel.CreateBasicProperties();

            if (isQueue)
            {
                channel.BasicPublish("", destination, props, body);
            }
            else
            {
                channel.BasicPublish(destination, routingKey, props, body);
            }
        }

        private IConnection ResolveConnection(string connectionId)
        {
            if (globalConnection == null)
            {
                lock (lockObject)
                {
                    globalConnection ??= connectionFactory.CreateConnection($"Publisher_{connectionId}");
                }
            }
            return globalConnection;
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