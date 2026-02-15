using EasyRabbitFlow.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System;
using System.Collections.Generic;
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
        /// Asynchronously publishes a single message to a RabbitMQ exchange using publisher confirms.
        /// The <c>await</c> completes only after the broker confirms receipt of the message.
        /// </summary>
        /// <typeparam name="TEvent">The type of the message to publish.</typeparam>
        /// <param name="message">The message to publish.</param>
        /// <param name="exchangeName">The name of the exchange to publish the message to.</param>
        /// <param name="routingKey">The routing key for the message.</param>
        /// <param name="publisherId">An optional identifier for the publisher connection.</param>
        /// <param name="jsonOptions">Optional JSON serializer options.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A <see cref="PublishResult"/> describing the outcome of the publish operation.</returns>
        Task<PublishResult> PublishAsync<TEvent>(TEvent message, string exchangeName, string routingKey, string publisherId = "", JsonSerializerOptions? jsonOptions = null, CancellationToken cancellationToken = default) where TEvent : class;


        /// <summary>
        /// Asynchronously publishes a single message to a RabbitMQ queue using publisher confirms.
        /// The <c>await</c> completes only after the broker confirms receipt of the message.
        /// </summary>
        /// <typeparam name="TEvent">The type of the message to publish.</typeparam>
        /// <param name="message">The message to publish.</param>
        /// <param name="queueName">The name of the queue to publish the message to.</param>
        /// <param name="publisherId">An optional identifier for the publisher connection.</param>
        /// <param name="jsonOptions">Optional JSON serializer options.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A <see cref="PublishResult"/> describing the outcome of the publish operation.</returns>
        Task<PublishResult> PublishAsync<TEvent>(TEvent message, string queueName, string publisherId = "", JsonSerializerOptions? jsonOptions = null, CancellationToken cancellationToken = default) where TEvent : class;

        /// <summary>
        /// Asynchronously publishes a batch of messages to a RabbitMQ exchange.
        /// <list type="bullet">
        /// <item><c>Transactional</c> (default): All messages are published atomically within a single AMQP transaction.
        /// If any message fails, the entire batch is rolled back and no messages are delivered.</item>
        /// <item><c>Confirm</c>: Each message is individually confirmed by the broker.
        /// A failure mid-batch does not roll back previously confirmed messages.</item>
        /// </list>
        /// </summary>
        /// <typeparam name="TEvent">The type of the messages to publish.</typeparam>
        /// <param name="messages">The collection of messages to publish.</param>
        /// <param name="exchangeName">The name of the exchange to publish messages to.</param>
        /// <param name="routingKey">The routing key for the messages.</param>
        /// <param name="channelMode">The channel mode for the batch operation. Defaults to <see cref="ChannelMode.Transactional"/>.</param>
        /// <param name="publisherId">An optional identifier for the publisher connection.</param>
        /// <param name="jsonOptions">Optional JSON serializer options.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A <see cref="BatchPublishResult"/> describing the outcome of the batch publish operation.</returns>
        Task<BatchPublishResult> PublishBatchAsync<TEvent>(IReadOnlyList<TEvent> messages, string exchangeName, string routingKey, ChannelMode channelMode = ChannelMode.Transactional, string publisherId = "", JsonSerializerOptions? jsonOptions = null, CancellationToken cancellationToken = default) where TEvent : class;

        /// <summary>
        /// Asynchronously publishes a batch of messages to a RabbitMQ queue.
        /// <list type="bullet">
        /// <item><c>Transactional</c> (default): All messages are published atomically within a single AMQP transaction.
        /// If any message fails, the entire batch is rolled back and no messages are delivered.</item>
        /// <item><c>Confirm</c>: Each message is individually confirmed by the broker.
        /// A failure mid-batch does not roll back previously confirmed messages.</item>
        /// </list>
        /// </summary>
        /// <typeparam name="TEvent">The type of the messages to publish.</typeparam>
        /// <param name="messages">The collection of messages to publish.</param>
        /// <param name="queueName">The name of the queue to publish messages to.</param>
        /// <param name="channelMode">The channel mode for the batch operation. Defaults to <see cref="ChannelMode.Transactional"/>.</param>
        /// <param name="publisherId">An optional identifier for the publisher connection.</param>
        /// <param name="jsonOptions">Optional JSON serializer options.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A <see cref="BatchPublishResult"/> describing the outcome of the batch publish operation.</returns>
        Task<BatchPublishResult> PublishBatchAsync<TEvent>(IReadOnlyList<TEvent> messages, string queueName, ChannelMode channelMode = ChannelMode.Transactional, string publisherId = "", JsonSerializerOptions? jsonOptions = null, CancellationToken cancellationToken = default) where TEvent : class;
    }

    internal sealed class RabbitFlowPublisher : IRabbitFlowPublisher
    {
        private readonly ConnectionFactory connectionFactory;
        private readonly JsonSerializerOptions jsonOptions;
        private readonly PublisherOptions publisherOptions;
        private readonly ILogger<RabbitFlowPublisher> logger;
        private IConnection? globalConnection;
        private readonly SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);

        private static readonly CreateChannelOptions ConfirmChannelOptions =
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);

        private static readonly CreateChannelOptions PlainChannelOptions =
            new CreateChannelOptions(publisherConfirmationsEnabled: false, publisherConfirmationTrackingEnabled: false);

        public RabbitFlowPublisher(ConnectionFactory connectionFactory, ILogger<RabbitFlowPublisher> logger, PublisherOptions? publisherOptions = null, [FromKeyedServices("RabbitFlowJsonSerializer")] JsonSerializerOptions? jsonOptions = null)
        {
            this.connectionFactory = connectionFactory;
            this.logger = logger;
            this.jsonOptions = jsonOptions ?? JsonSerializerOptions.Web;
            this.publisherOptions = publisherOptions ?? new PublisherOptions();
        }

        public async Task<PublishResult> PublishAsync<TEvent>(TEvent @event, string exchangeName, string routingKey = "", string publisherId = "", JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) where TEvent : class
        {
            return await PublishMessageAsync(@event, exchangeName, routingKey, publisherId, jsonSerializerOptions, isQueue: false, cancellationToken);
        }

        public async Task<PublishResult> PublishAsync<TEvent>(TEvent @event, string queueName, string publisherId = "", JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) where TEvent : class
        {
            return await PublishMessageAsync(@event, queueName, "", publisherId, jsonSerializerOptions, isQueue: true, cancellationToken);
        }

        public async Task<BatchPublishResult> PublishBatchAsync<TEvent>(IReadOnlyList<TEvent> messages, string exchangeName, string routingKey, ChannelMode channelMode = ChannelMode.Transactional, string publisherId = "", JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) where TEvent : class
        {
            return await PublishBatchInternalAsync(messages, exchangeName, routingKey, channelMode, publisherId, jsonSerializerOptions, isQueue: false, cancellationToken);
        }

        public async Task<BatchPublishResult> PublishBatchAsync<TEvent>(IReadOnlyList<TEvent> messages, string queueName, ChannelMode channelMode = ChannelMode.Transactional, string publisherId = "", JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) where TEvent : class
        {
            return await PublishBatchInternalAsync(messages, queueName, "", channelMode, publisherId, jsonSerializerOptions, isQueue: true, cancellationToken);
        }

        private async Task<PublishResult> PublishMessageAsync<TEvent>(TEvent @event, string destination, string routingKey, string publisherId, JsonSerializerOptions? jsonSerializerOptions, bool isQueue, CancellationToken cancellationToken = default) where TEvent : class
        {
            if (@event is null)
            {
                throw new ArgumentNullException(nameof(@event));
            }

            publisherId ??= isQueue ? destination : string.Concat(destination, "_", routingKey);

            string? messageId = publisherOptions.IdempotencyEnabled ? Guid.NewGuid().ToString("N") : null;

            var connection = await ResolveConnection(publisherId);

            var serializerOptions = jsonSerializerOptions ?? jsonOptions;

            using var channel = await connection.CreateChannelAsync(ConfirmChannelOptions, cancellationToken);

            try
            {
                await PublishToChannelAsync(channel, @event, destination, routingKey, serializerOptions, isQueue, messageId, cancellationToken);

                logger.LogDebug("[RABBIT-FLOW]: Message of type {MessageType} published to {Destination}. MessageId={MessageId}",
                    typeof(TEvent).FullName, destination, messageId ?? "(none)");

                return PublishResult.Successful(destination, routingKey, messageId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[RABBIT-FLOW]: Error publishing message to {Destination}", destination);

                return PublishResult.Failed(destination, routingKey, messageId, ex);
            }
            finally
            {
                if (publisherOptions.DisposePublisherConnection && globalConnection != null)
                {
                    await DisposeGlobalConnection(cancellationToken);
                }
            }
        }

        private async Task<BatchPublishResult> PublishBatchInternalAsync<TEvent>(IReadOnlyList<TEvent> messages, string destination, string routingKey, ChannelMode channelMode, string publisherId, JsonSerializerOptions? jsonSerializerOptions, bool isQueue, CancellationToken cancellationToken = default) where TEvent : class
        {
            if (messages is null || messages.Count == 0)
            {
                throw new ArgumentException("Messages collection must not be null or empty.", nameof(messages));
            }

            publisherId ??= isQueue ? destination : string.Concat(destination, "_", routingKey);

            var messageIds = new List<string>(messages.Count);

            var connection = await ResolveConnection(publisherId);

            var serializerOptions = jsonSerializerOptions ?? jsonOptions;

            var channelOptions = channelMode == ChannelMode.Transactional ? PlainChannelOptions : ConfirmChannelOptions;

            using var channel = await connection.CreateChannelAsync(channelOptions, cancellationToken);

            try
            {
                if (channelMode == ChannelMode.Transactional)
                {
                    await channel.TxSelectAsync(cancellationToken);
                }

                for (var i = 0; i < messages.Count; i++)
                {
                    var msg = messages[i];

                    if (msg is null)
                    {
                        throw new ArgumentNullException($"messages[{i}]", "Message at index " + i + " is null.");
                    }

                    string? messageId = publisherOptions.IdempotencyEnabled ? Guid.NewGuid().ToString("N") : null;

                    await PublishToChannelAsync(channel, msg, destination, routingKey, serializerOptions, isQueue, messageId, cancellationToken);

                    if (messageId != null)
                    {
                        messageIds.Add(messageId);
                    }
                }

                if (channelMode == ChannelMode.Transactional)
                {
                    await channel.TxCommitAsync(cancellationToken);
                }

                logger.LogDebug("[RABBIT-FLOW]: Batch of {Count} messages published to {Destination}. Mode={Mode}",
                    messages.Count, destination, channelMode);

                return BatchPublishResult.Successful(destination, routingKey, channelMode, messages.Count, messageIds);
            }
            catch (Exception ex)
            {
                if (channelMode == ChannelMode.Transactional)
                {
                    try
                    {
                        await channel.TxRollbackAsync(cancellationToken);
                    }
                    catch (Exception rollbackEx)
                    {
                        logger.LogWarning(rollbackEx, "[RABBIT-FLOW]: Transaction rollback failed during batch publish.");
                    }
                }

                logger.LogError(ex, "[RABBIT-FLOW]: Error publishing batch to {Destination}. Mode={Mode}", destination, channelMode);

                return BatchPublishResult.Failed(destination, routingKey, channelMode, messages.Count, messageIds, ex);
            }
            finally
            {
                if (publisherOptions.DisposePublisherConnection && globalConnection != null)
                {
                    await DisposeGlobalConnection(cancellationToken);
                }
            }
        }


        private ValueTask PublishToChannelAsync<TEvent>(IChannel channel, TEvent @event, string destination, string routingKey, JsonSerializerOptions serializerOptions, bool isQueue, string? messageId, CancellationToken cancellationToken = default) where TEvent : class
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(@event, serializerOptions);

            var exchange = isQueue ? "" : destination;

            var rk = isQueue ? destination : routingKey;

            if (messageId != null)
            {
                var properties = new BasicProperties { MessageId = messageId };

                return channel.BasicPublishAsync(exchange, rk, false, properties, body, cancellationToken);
            }

            return channel.BasicPublishAsync(exchange, rk, body, cancellationToken);
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