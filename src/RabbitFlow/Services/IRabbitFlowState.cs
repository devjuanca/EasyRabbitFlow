using EasyRabbitFlow.Settings;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EasyRabbitFlow.Services
{
    public interface IRabbitFlowState
    {
        /// <summary>
        /// Gets a full snapshot of a queue's state — existence, message count, and consumer count —
        /// in a single broker round trip, instead of one call per metric.
        /// Does not throw when the queue is missing: <see cref="QueueState.Exists"/> is <c>false</c> instead.
        /// </summary>
        /// <param name="queueName">The name of the queue to inspect.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A <see cref="QueueState"/> snapshot.</returns>
        Task<QueueState> GetQueueStateAsync(string queueName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets full state snapshots for several queues reusing a single broker connection.
        /// Missing queues are reported with <see cref="QueueState.Exists"/> set to <c>false</c> rather than throwing.
        /// </summary>
        /// <param name="queueNames">The names of the queues to inspect.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>One <see cref="QueueState"/> per queue, in input order.</returns>
        Task<IReadOnlyList<QueueState>> GetQueuesStateAsync(IEnumerable<string> queueNames, CancellationToken cancellationToken = default);

        /// <summary>
        /// Checks if a specific queue is empty.
        /// </summary>
        /// <param name="queueName">The name of the queue to check.</param>
        /// <param name="cancellationToken"></param>
        /// <returns>True if the queue is empty, False if it contains messages.</returns>
        Task<bool> IsEmptyQueueAsync(string queueName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the number of messages in a specific queue.
        /// </summary>
        /// <param name="queueName">The name of the queue to get the message count from.</param>
        /// <param name="cancellationToken"></param>
        /// <returns>The number of messages in the queue.</returns>
        Task<uint> GetQueueLengthAsync(string queueName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the number of consumers for a specific queue.
        /// </summary>
        /// <param name="queueName">The name of the queue to get the consumers count from.</param>
        /// <param name="cancellationToken"></param>
        /// <returns>The number of consumers for the queue.</returns>
        Task<uint> GetConsumersCountAsync(string queueName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Checks if a specific queue has consumers.
        /// </summary>
        /// <param name="queueName">The name of the queue to check.</param>
        /// <param name="cancellationToken"></param>
        /// <returns>True if the queue has consumers, False otherwise.</returns>
        Task<bool> HasConsumersAsync(string queueName, CancellationToken cancellationToken = default);
    }

    internal sealed class RabbitFlowState : IRabbitFlowState
    {
        private readonly ConnectionFactory _connectionFactory;

        public RabbitFlowState(ConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task<QueueState> GetQueueStateAsync(string queueName, CancellationToken cancellationToken = default)
        {
            using var connection = await _connectionFactory.CreateConnectionAsync($"state-connection-{Guid.NewGuid():N}", cancellationToken);

            return await ReadQueueStateAsync(connection, queueName, cancellationToken);
        }

        public async Task<IReadOnlyList<QueueState>> GetQueuesStateAsync(IEnumerable<string> queueNames, CancellationToken cancellationToken = default)
        {
            if (queueNames is null)
            {
                throw new ArgumentNullException(nameof(queueNames));
            }

            using var connection = await _connectionFactory.CreateConnectionAsync($"state-connection-{Guid.NewGuid():N}", cancellationToken);

            var states = new List<QueueState>();

            foreach (var queueName in queueNames)
            {
                states.Add(await ReadQueueStateAsync(connection, queueName, cancellationToken));
            }

            return states;
        }

        private static async Task<QueueState> ReadQueueStateAsync(IConnection connection, string queueName, CancellationToken cancellationToken)
        {
            // A passive declare returns message and consumer counts in one round trip.
            // It closes the channel on a 404, so each queue gets its own channel.
            using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

            try
            {
                var ok = await channel.QueueDeclarePassiveAsync(queueName, cancellationToken);

                return new QueueState(queueName, exists: true, ok.MessageCount, ok.ConsumerCount);
            }
            catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 404)
            {
                return new QueueState(queueName, exists: false, 0, 0);
            }
        }

        public async Task<uint> GetQueueLengthAsync(string queueName, CancellationToken cancellationToken = default)
        {
            using var connection = await _connectionFactory.CreateConnectionAsync($"state-connection-{Guid.NewGuid():N}", cancellationToken);

            using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

            return await channel.MessageCountAsync(queueName, cancellationToken: cancellationToken);

        }

        public async Task<bool> IsEmptyQueueAsync(string queueName, CancellationToken cancellationToken = default)
        {
            using var connection = await _connectionFactory.CreateConnectionAsync($"state-connection-{Guid.NewGuid():N}", cancellationToken);

            using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

            var messagesCount = await channel.MessageCountAsync(queueName, cancellationToken: cancellationToken);

            return messagesCount == 0;
        }

        public async Task<uint> GetConsumersCountAsync(string queueName, CancellationToken cancellationToken = default)
        {
            using var connection = await _connectionFactory.CreateConnectionAsync($"state-connection-{Guid.NewGuid():N}");

            using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

            return await channel.ConsumerCountAsync(queueName, cancellationToken: cancellationToken);
        }

        public async Task<bool> HasConsumersAsync(string queueName, CancellationToken cancellationToken = default)
        {
            using var connection = await _connectionFactory.CreateConnectionAsync($"state-connection-{Guid.NewGuid():N}");

            using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

            var consumersCount = await channel.ConsumerCountAsync(queueName, cancellationToken: cancellationToken);

            return consumersCount > 0;
        }

    }
}
