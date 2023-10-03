using RabbitMQ.Client;

namespace RabbitFlow.Services
{
    public interface IRabbitFlowState
    {
        /// <summary>
        /// Checks if a specific queue is empty.
        /// </summary>
        /// <param name="queueName">The name of the queue to check.</param>
        /// <returns>True if the queue is empty, False if it contains messages.</returns>
        bool IsEmptyQueue(string queueName);

        /// <summary>
        /// Gets the number of messages in a specific queue.
        /// </summary>
        /// <param name="queueName">The name of the queue to get the message count from.</param>
        /// <returns>The number of messages in the queue.</returns>
        uint GetQueueLength(string queueName);
    }

    internal class RabbitFlowState : IRabbitFlowState
    {
        private readonly ConnectionFactory _connectionFactory;

        public RabbitFlowState(ConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public uint GetQueueLength(string queueName)
        {
            using var connection = _connectionFactory.CreateConnection("state-connection");

            using var channel = connection.CreateModel();

            var queueInfo = channel.QueueDeclarePassive(queueName);

            return queueInfo.MessageCount;
        }

        public bool IsEmptyQueue(string queueName)
        {
            using var connection = _connectionFactory.CreateConnection("state-connection");

            using var channel = connection.CreateModel();

            var queueInfo = channel.QueueDeclarePassive(queueName);

            return queueInfo.MessageCount == 0;
        }

    }
}
