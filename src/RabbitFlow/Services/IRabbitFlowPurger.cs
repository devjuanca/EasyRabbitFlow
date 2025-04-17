using RabbitMQ.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EasyRabbitFlow.Services
{
    /// <summary>
    ///  Service for purging messages from RabbitMQ queues.
    /// </summary>
    public interface IRabbitFlowPurger
    {
        /// <summary>
        /// Purges all messages from the specified RabbitMQ queue.
        /// </summary>
        /// <param name="queueName">The name of the queue to purge.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>

        Task PurgeMessagesAsync(string queueName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Purges all messages from the specified RabbitMQ queues.
        /// </summary>
        /// <param name="queueNames">The names of the queues to purge.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        Task PurgeMessagesAsync(IEnumerable<string> queueNames, CancellationToken cancellationToken = default);
    }

    internal sealed class RabbitFlowPurger : IRabbitFlowPurger
    {
        private readonly ConnectionFactory _connectionFactory;

        public RabbitFlowPurger(ConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }
        public async Task PurgeMessagesAsync(string queueName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(queueName))
            {
                throw new ArgumentException("Queue name must not be null or empty.", nameof(queueName));
            }

            try
            {
                using var connection = await _connectionFactory.CreateConnectionAsync($"purger-{Guid.NewGuid():N}", cancellationToken);

                using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

                await channel.QueuePurgeAsync(queueName, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to purge queue '{queueName}'.", ex);
            }
        }

        public async Task PurgeMessagesAsync(IEnumerable<string> queueNames, CancellationToken cancellationToken = default)
        {
            if (queueNames is null)
            {
                throw new ArgumentNullException(nameof(queueNames));
            }

            var queueList = queueNames.Where(q => !string.IsNullOrWhiteSpace(q)).Distinct().ToList();

            if (queueList.Count == 0)
            {
                return;
            }
            try
            {
                using var connection = await _connectionFactory.CreateConnectionAsync($"purger-{Guid.NewGuid():N}", cancellationToken);

                using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

                foreach (var queueName in queueList)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await channel.QueuePurgeAsync(queueName, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to purge one or more queues.", ex);
            }
        }
    }
}
