using EasyRabbitFlow.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        /// <param name="messageId">
        /// Optional deterministic identifier for the message. Pass a key derived from your business data
        /// (e.g. <c>OrderId + Operation</c>) when you need true publish-side idempotency — retries of the same logical
        /// publish will then carry the same <c>MessageId</c> and consumers can deduplicate. When <c>null</c> (default),
        /// a unique GUID is generated automatically.
        /// </param>
        /// <param name="correlationId">An optional correlation identifier for tracing related messages.</param>
        /// <param name="options">
        /// Optional publish settings: AMQP metadata (delivery mode, custom headers, type, app-id, expiration,
        /// priority, timestamp, reply-to, content-type) and a per-call JSON serializer override.
        /// When <c>null</c>, library defaults are used (transient delivery, no custom headers,
        /// JSON serializer from DI).
        /// </param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A <see cref="PublishResult"/> describing the outcome of the publish operation.</returns>
        Task<PublishResult> PublishAsync<TEvent>(
            TEvent message, 
            string exchangeName, 
            string routingKey, 
            string? messageId = null, 
            string? correlationId = null, 
            PublishOptions? options = null, 
            CancellationToken cancellationToken = default) where TEvent : class;


        /// <summary>
        /// Asynchronously publishes a single message to a RabbitMQ queue using publisher confirms.
        /// The <c>await</c> completes only after the broker confirms receipt of the message.
        /// </summary>
        /// <typeparam name="TEvent">The type of the message to publish.</typeparam>
        /// <param name="message">The message to publish.</param>
        /// <param name="queueName">The name of the queue to publish the message to.</param>
        /// <param name="messageId">
        /// Optional deterministic identifier for the message. Pass a key derived from your business data
        /// (e.g. <c>OrderId + Operation</c>) when you need true publish-side idempotency — retries of the same logical
        /// publish will then carry the same <c>MessageId</c> and consumers can deduplicate. When <c>null</c> (default),
        /// a unique GUID is generated automatically.
        /// </param>
        /// <param name="correlationId">An optional correlation identifier for tracing related messages.</param>
        /// <param name="options">
        /// Optional publish settings: AMQP metadata (delivery mode, custom headers, type, app-id, expiration,
        /// priority, timestamp, reply-to, content-type) and a per-call JSON serializer override.
        /// When <c>null</c>, library defaults are used (transient delivery, no custom headers,
        /// JSON serializer from DI).
        /// </param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A <see cref="PublishResult"/> describing the outcome of the publish operation.</returns>
        Task<PublishResult> PublishAsync<TEvent>(
            TEvent message, 
            string queueName, 
            string? messageId = null, 
            string? correlationId = null, 
            PublishOptions? options = null, 
            CancellationToken cancellationToken = default) where TEvent : class;

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
        /// <param name="messageIdSelector">
        /// Optional selector that produces a deterministic <c>MessageId</c> per event (for true idempotency from business data).
        /// When <c>null</c> (default), each message gets an auto-generated unique GUID. The selector must return a
        /// non-empty string; an empty/null result causes the batch to fail (rolled back in <c>Transactional</c> mode).
        /// </param>
        /// <param name="correlationId">An optional correlation identifier shared by all messages in the batch.</param>
        /// <param name="options">
        /// Optional publish settings applied to every message in the batch: AMQP metadata (delivery mode, custom
        /// headers, type, app-id, expiration, priority, timestamp, reply-to, content-type) and a per-call JSON
        /// serializer override. If you need per-message metadata, publish each message individually with
        /// <see cref="PublishAsync{TEvent}(TEvent, string, string, string?, string?, PublishOptions?, CancellationToken)"/> instead.
        /// </param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A <see cref="BatchPublishResult"/> describing the outcome of the batch publish operation.</returns>
        Task<BatchPublishResult> PublishBatchAsync<TEvent>(
            IReadOnlyList<TEvent> messages, 
            string exchangeName, 
            string routingKey, 
            ChannelMode channelMode = ChannelMode.Transactional, 
            Func<TEvent, string>? messageIdSelector = null, 
            string? correlationId = null, 
            PublishOptions? options = null, 
            CancellationToken cancellationToken = default) where TEvent : class;

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
        /// <param name="messageIdSelector">
        /// Optional selector that produces a deterministic <c>MessageId</c> per event (for true idempotency from business data).
        /// When <c>null</c> (default), each message gets an auto-generated unique GUID. The selector must return a
        /// non-empty string; an empty/null result causes the batch to fail (rolled back in <c>Transactional</c> mode).
        /// </param>
        /// <param name="correlationId">An optional correlation identifier shared by all messages in the batch.</param>
        /// <param name="options">
        /// Optional publish settings applied to every message in the batch: AMQP metadata (delivery mode, custom
        /// headers, type, app-id, expiration, priority, timestamp, reply-to, content-type) and a per-call JSON
        /// serializer override. If you need per-message metadata, publish each message individually with
        /// <see cref="PublishAsync{TEvent}(TEvent, string, string?, string?, PublishOptions?, CancellationToken)"/> instead.
        /// </param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A <see cref="BatchPublishResult"/> describing the outcome of the batch publish operation.</returns>
        Task<BatchPublishResult> PublishBatchAsync<TEvent>(
            IReadOnlyList<TEvent> messages, 
            string queueName, 
            ChannelMode channelMode = ChannelMode.Transactional, 
            Func<TEvent, string>? messageIdSelector = null, 
            string? correlationId = null, 
            PublishOptions? options = null, 
            CancellationToken cancellationToken = default) where TEvent : class;
    }

    internal sealed class RabbitFlowPublisher : IRabbitFlowPublisher
    {
        private readonly ConnectionFactory connectionFactory;
        private readonly JsonSerializerOptions jsonOptions;
        private readonly PublisherConnectionOptions publisherOptions;
        private readonly ILogger<RabbitFlowPublisher> logger;
        private IConnection? globalConnection;
        private readonly SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);

        private static readonly CreateChannelOptions ConfirmChannelOptions =
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);

        private static readonly CreateChannelOptions PlainChannelOptions =
            new CreateChannelOptions(publisherConfirmationsEnabled: false, publisherConfirmationTrackingEnabled: false);

        public RabbitFlowPublisher(ConnectionFactory connectionFactory, ILogger<RabbitFlowPublisher> logger, PublisherConnectionOptions? publisherOptions = null, [FromKeyedServices("RabbitFlowJsonSerializer")] JsonSerializerOptions? jsonOptions = null)
        {
            this.connectionFactory = connectionFactory;
            this.logger = logger;
            this.jsonOptions = jsonOptions ?? JsonSerializerOptions.Web;
            this.publisherOptions = publisherOptions ?? new PublisherConnectionOptions();
        }

        public async Task<PublishResult> PublishAsync<TEvent>(TEvent @event, string exchangeName, string routingKey = "", string? messageId = null, string? correlationId = null, PublishOptions? options = null, CancellationToken cancellationToken = default) where TEvent : class
        {
            return await PublishMessageAsync(@event, exchangeName, routingKey, messageId, correlationId, options, isQueue: false, cancellationToken);
        }

        public async Task<PublishResult> PublishAsync<TEvent>(TEvent @event, string queueName, string? messageId = null, string? correlationId = null, PublishOptions? options = null, CancellationToken cancellationToken = default) where TEvent : class
        {
            return await PublishMessageAsync(@event, queueName, "", messageId, correlationId, options, isQueue: true, cancellationToken);
        }

        public async Task<BatchPublishResult> PublishBatchAsync<TEvent>(IReadOnlyList<TEvent> messages, string exchangeName, string routingKey, ChannelMode channelMode = ChannelMode.Transactional, Func<TEvent, string>? messageIdSelector = null, string? correlationId = null, PublishOptions? options = null, CancellationToken cancellationToken = default) where TEvent : class
        {
            return await PublishBatchInternalAsync(messages, exchangeName, routingKey, channelMode, messageIdSelector, correlationId, options, isQueue: false, cancellationToken);
        }

        public async Task<BatchPublishResult> PublishBatchAsync<TEvent>(IReadOnlyList<TEvent> messages, string queueName, ChannelMode channelMode = ChannelMode.Transactional, Func<TEvent, string>? messageIdSelector = null, string? correlationId = null, PublishOptions? options = null, CancellationToken cancellationToken = default) where TEvent : class
        {
            return await PublishBatchInternalAsync(messages, queueName, "", channelMode, messageIdSelector, correlationId, options, isQueue: true, cancellationToken);
        }

        private async Task<PublishResult> PublishMessageAsync<TEvent>(TEvent @event, string destination, string routingKey, string? messageId, string? correlationId, PublishOptions? options, bool isQueue, CancellationToken cancellationToken = default) where TEvent : class
        {
            if (@event is null)
            {
                throw new ArgumentNullException(nameof(@event));
            }

            var resolvedMessageId = string.IsNullOrEmpty(messageId) ? Guid.NewGuid().ToString("N") : messageId!;

            using var activity = RabbitFlowDiagnostics.StartPublish(destination, routingKey, resolvedMessageId, correlationId);

            var connection = await ResolveConnection(publisherOptions.PublisherId, cancellationToken);

            var serializerOptions = options?.JsonOptions ?? jsonOptions;

            using var channel = await connection.CreateChannelAsync(ConfirmChannelOptions, cancellationToken);

            try
            {
                await PublishToChannelAsync(channel, @event, destination, routingKey, serializerOptions, isQueue, resolvedMessageId, correlationId, options, cancellationToken);

                logger.LogDebug("[RABBIT-FLOW]: Message of type {MessageType} published to {Destination}. MessageId={MessageId}",
                    typeof(TEvent).FullName, destination, resolvedMessageId);

                RabbitFlowDiagnostics.PublishedMessages.Add(1, new KeyValuePair<string, object?>("destination", destination));

                return PublishResult.Successful(destination, routingKey, resolvedMessageId);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                RabbitFlowDiagnostics.PublishFailures.Add(1, new KeyValuePair<string, object?>("destination", destination));

                logger.LogError(ex, "[RABBIT-FLOW]: Error publishing message to {Destination}", destination);

                return PublishResult.Failed(destination, routingKey, resolvedMessageId, ex);
            }
            finally
            {
                if (publisherOptions.DisposePublisherConnection && globalConnection != null)
                {
                    await DisposeGlobalConnection(cancellationToken);
                }
            }
        }

        private async Task<BatchPublishResult> PublishBatchInternalAsync<TEvent>(IReadOnlyList<TEvent> messages, string destination, string routingKey, ChannelMode channelMode, Func<TEvent, string>? messageIdSelector, string? correlationId, PublishOptions? options, bool isQueue, CancellationToken cancellationToken = default) where TEvent : class
        {
            if (messages is null || messages.Count == 0)
            {
                throw new ArgumentException("Messages collection must not be null or empty.", nameof(messages));
            }

            var messageIds = new List<string>(messages.Count);

            using var activity = RabbitFlowDiagnostics.StartPublish(destination, routingKey, messageId: null, correlationId, messages.Count);

            var connection = await ResolveConnection(publisherOptions.PublisherId, cancellationToken);

            var serializerOptions = options?.JsonOptions ?? jsonOptions;

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

                    string messageId;
                    if (messageIdSelector != null)
                    {
                        var selected = messageIdSelector(msg);
                        if (string.IsNullOrEmpty(selected))
                        {
                            throw new ArgumentException($"messageIdSelector returned a null or empty MessageId for messages[{i}].", nameof(messageIdSelector));
                        }
                        messageId = selected;
                    }
                    else
                    {
                        messageId = Guid.NewGuid().ToString("N");
                    }

                    await PublishToChannelAsync(channel, msg, destination, routingKey, serializerOptions, isQueue, messageId, correlationId, options, cancellationToken);

                    messageIds.Add(messageId);
                }

                if (channelMode == ChannelMode.Transactional)
                {
                    await channel.TxCommitAsync(cancellationToken);
                }

                logger.LogDebug("[RABBIT-FLOW]: Batch of {Count} messages published to {Destination}. Mode={Mode}",
                    messages.Count, destination, channelMode);

                RabbitFlowDiagnostics.PublishedMessages.Add(messages.Count, new KeyValuePair<string, object?>("destination", destination));

                return BatchPublishResult.Successful(destination, routingKey, channelMode, messages.Count, messageIds);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                RabbitFlowDiagnostics.PublishFailures.Add(1, new KeyValuePair<string, object?>("destination", destination));

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


        private ValueTask PublishToChannelAsync<TEvent>(IChannel channel, TEvent @event, string destination, string routingKey, JsonSerializerOptions serializerOptions, bool isQueue, string? messageId, string? correlationId, PublishOptions? options, CancellationToken cancellationToken = default) where TEvent : class
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(@event, serializerOptions);

            var exchange = isQueue ? "" : destination;

            var rk = isQueue ? destination : routingKey;

            var hasTraceContext = RabbitFlowDiagnostics.HasCurrentW3CContext;

            if (messageId == null && correlationId == null && options == null && !hasTraceContext)
            {
                return channel.BasicPublishAsync(exchange, rk, body, cancellationToken);
            }

            var properties = new BasicProperties
            {
                MessageId = messageId,
                CorrelationId = correlationId
            };

            if (options != null)
            {
                properties.DeliveryMode = (DeliveryModes)(byte)options.DeliveryMode;

                if (options.Type != null) 
                    properties.Type = options.Type;

                if (options.AppId != null) 
                    properties.AppId = options.AppId;
                
                if (options.Priority.HasValue) 
                    properties.Priority = options.Priority.Value;
                
                if (options.ReplyTo != null) 
                    properties.ReplyTo = options.ReplyTo;

                if (options.ContentType != null) 
                    properties.ContentType = options.ContentType;

                if (options.Expiration.HasValue)
                {
                    var ms = (long)options.Expiration.Value.TotalMilliseconds;
                    properties.Expiration = ms.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                
                if (options.Timestamp.HasValue)
                {
                    properties.Timestamp = new AmqpTimestamp(options.Timestamp.Value.ToUnixTimeSeconds());
                }
            }

            if (hasTraceContext)
            {
                // Copy caller headers before injecting so the user's dictionary is never mutated
                var headers = options?.Headers != null
                    ? new Dictionary<string, object?>(options.Headers)
                    : new Dictionary<string, object?>();

                RabbitFlowDiagnostics.InjectContext(headers);

                properties.Headers = headers;
            }
            else if (options?.Headers != null)
            {
                properties.Headers = options.Headers;
            }

            return channel.BasicPublishAsync(exchange, rk, false, properties, body, cancellationToken);
        }

        private async Task<IConnection> ResolveConnection(string connectionId, CancellationToken cancellationToken = default)
        {
            // The RabbitMQ client's automatic recovery is disabled (EasyRabbitFlow manages recovery itself), so this
            // long-lived publisher connection is not healed by the client when it drops — it must be re-created here.
            // Fast path: reuse it while open.
            if (globalConnection != null && globalConnection.IsOpen)
            {
                return globalConnection;
            }

            await semaphore.WaitAsync(cancellationToken);

            try
            {
                // Replace a connection that closed since the last publish.
                if (globalConnection != null && !globalConnection.IsOpen)
                {
                    try { await globalConnection.DisposeAsync(); } catch { }

                    globalConnection = null;
                }

                globalConnection ??= await connectionFactory.CreateConnectionAsync($"Publisher_{connectionId}", cancellationToken);
            }
            finally
            {
                semaphore.Release();
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