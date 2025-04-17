using EasyRabbitFlow.Settings;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System;
using System.Text.Json;
using System.Threading;
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
        /// <param name="cancellationToken"></param>
        /// <returns>A task representing the asynchronous operation. The task result is <c>true</c> if the message was published successfully; otherwise, <c>false</c>.</returns>
        Task<bool> PublishAsync<TEvent>(TEvent message, string exchangeName, string routingKey, string publisherId = "", JsonSerializerOptions? jsonOptions = null, CancellationToken cancellationToken = default) where TEvent : class;


        /// <summary>
        /// Asynchronously publishes a message to a RabbitMQ queue.
        /// </summary>
        /// <typeparam name="TEvent">The type of the message to publish.</typeparam>
        /// <param name="message">The message to publish.</param>
        /// <param name="queueName">The name of the queue to publish the message to.</param>
        /// <param name="publisherId">An optional identifier for the publisher connection.</param>
        /// <param name="jsonOptions">Optional JSON serializer options.</param>
        /// <param name="cancellationToken"></param>
        /// <returns>A task representing the asynchronous operation. The task result is <c>true</c> if the message was published successfully; otherwise, <c>false</c>.</returns>
        Task<bool> PublishAsync<TEvent>(TEvent message, string queueName, string publisherId = "", JsonSerializerOptions? jsonOptions = null, CancellationToken cancellationToken = default) where TEvent : class;
    }

    internal sealed class RabbitFlowPublisher : IRabbitFlowPublisher
    {
        private readonly ConnectionFactory connectionFactory;
        private readonly JsonSerializerOptions jsonOptions;
        private readonly PublisherOptions publisherOptions;
        private readonly ILogger<RabbitFlowPublisher> logger;
        private IConnection? globalConnection;
        private readonly SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);

        public RabbitFlowPublisher(ConnectionFactory connectionFactory, ILogger<RabbitFlowPublisher> logger, PublisherOptions? publisherOptions = null, JsonSerializerOptions? jsonOptions = null)
        {
            this.connectionFactory = connectionFactory;
            this.logger = logger;
            this.jsonOptions = jsonOptions ?? new JsonSerializerOptions();
            this.publisherOptions = publisherOptions ?? new PublisherOptions();
        }

        public async Task<bool> PublishAsync<TEvent>(TEvent @event, string exchangeName, string routingKey = "", string publisherId = "", JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) where TEvent : class
        {
            return await PublishMessageAsync(@event, exchangeName, routingKey, publisherId, jsonSerializerOptions, isQueue: false, cancellationToken);
        }

        public async Task<bool> PublishAsync<TEvent>(TEvent @event, string queueName, string publisherId = "", JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) where TEvent : class
        {
            return await PublishMessageAsync(@event, queueName, "", publisherId, jsonSerializerOptions, isQueue: true, cancellationToken);
        }

        private async Task<bool> PublishMessageAsync<TEvent>(TEvent @event, string destination, string routingKey, string publisherId, JsonSerializerOptions? jsonSerializerOptions, bool isQueue, CancellationToken cancellationToken = default) where TEvent : class
        {
            if (@event is null)
            {
                throw new ArgumentNullException(nameof(@event));
            }

            publisherId ??= isQueue ? destination : string.Concat(destination, "_", routingKey);

            var connection = await ResolveConnection(publisherId);

            var serializerOptions = jsonSerializerOptions ?? jsonOptions;

            using var channel = await connection.CreateChannelAsync();

            try
            {
                if (publisherOptions.ChannelMode == ChannelMode.Transactional)
                {
                    await channel.TxSelectAsync(cancellationToken);

                    await PublishInternalAsync(channel, @event, destination, routingKey, serializerOptions, isQueue, cancellationToken);

                    await channel.TxCommitAsync(cancellationToken);
                }
                else
                {
                    await PublishInternalAsync(channel, @event, destination, routingKey, serializerOptions, isQueue, cancellationToken);
                }

                logger.LogInformation("Message of type: {messageType} was published.", typeof(TEvent).FullName);

                return true;
            }
            catch (Exception ex)
            {
                if (publisherOptions.ChannelMode == ChannelMode.Transactional)
                {
                    await channel.TxRollbackAsync(cancellationToken);
                }

                logger.LogError(ex, "[RABBIT-FLOW]: Error publishing a message");

                return false;
            }
            finally
            {
                if (publisherOptions.DisposePublisherConnection && globalConnection != null)
                {
                    await DisposeGlobalConnection(cancellationToken);
                }
            }
        }

        private ValueTask PublishInternalAsync<TEvent>(IChannel channel, TEvent @event, string destination, string routingKey, JsonSerializerOptions serializerOptions, bool isQueue, CancellationToken cancellationToken = default) where TEvent : class
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(@event, serializerOptions);

            if (isQueue)
            {
                return channel.BasicPublishAsync("", destination, body, cancellationToken);

            }
            else
            {
                return channel.BasicPublishAsync(destination, routingKey, body, cancellationToken);
            }
        }

        private async Task<IConnection> ResolveConnection(string connectionId, CancellationToken cancellationToken = default)
        {
            if (globalConnection == null)
            {
                await semaphore.WaitAsync();

                try
                {
                    globalConnection ??= await connectionFactory.CreateConnectionAsync($"Publisher_{connectionId}", cancellationToken);
                }
                finally
                {
                    semaphore.Release();
                }
            }
            return globalConnection;
        }

        private async Task DisposeGlobalConnection(CancellationToken cancellationToken = default)
        {
            if (globalConnection != null)
            {
                await semaphore.WaitAsync(cancellationToken);

                try
                {
                    await globalConnection.CloseAsync(cancellationToken);
                    await globalConnection.DisposeAsync();
                    globalConnection = null;
                }
                finally
                {
                    semaphore.Release();

                }
            }
        }
    }
}