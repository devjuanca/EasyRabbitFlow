using RabbitMQ.Client;
using System.Threading;
using System.Threading.Tasks;

namespace EasyRabbitFlow.Services
{
    public interface IRabbitFlowState
    {
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

    internal class RabbitFlowState : IRabbitFlowState
    {
        private readonly ConnectionFactory _connectionFactory;

        public RabbitFlowState(ConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task<uint> GetQueueLengthAsync(string queueName, CancellationToken cancellationToken = default)
        {
            using var connection = await _connectionFactory.CreateConnectionAsync("state-connection", cancellationToken);

            using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

            return await channel.MessageCountAsync(queueName, cancellationToken: cancellationToken);

        }

        public async Task<bool> IsEmptyQueueAsync(string queueName, CancellationToken cancellationToken = default)
        {
            using var connection = await _connectionFactory.CreateConnectionAsync("state-connection", cancellationToken);

            using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

            var messagesCount = await channel.MessageCountAsync(queueName, cancellationToken: cancellationToken);

            return messagesCount == 0;
        }

        public async Task<uint> GetConsumersCountAsync(string queueName, CancellationToken cancellationToken = default)
        {
            using var connection = await _connectionFactory.CreateConnectionAsync("state-connection");

            using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

            return await channel.ConsumerCountAsync(queueName, cancellationToken: cancellationToken);
        }

        public async Task<bool> HasConsumersAsync(string queueName, CancellationToken cancellationToken = default)
        {
            using var connection = await _connectionFactory.CreateConnectionAsync("state-connection");

            using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

            var consumersCount = await channel.ConsumerCountAsync(queueName, cancellationToken: cancellationToken);

            return consumersCount > 0;
        }

    }
}
