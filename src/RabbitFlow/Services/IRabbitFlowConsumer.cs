using EasyRabbitFlow.Settings;
using System.Threading;
using System.Threading.Tasks;

namespace EasyRabbitFlow.Services
{

    /// <summary>
    /// Defines a consumer interface for handling RabbitMQ messages of a specific event type.
    /// This interface should be implemented by classes that consume messages from RabbitMQ,
    /// allowing them to process messages of type <typeparamref name="TEvent"/> asynchronously.
    /// </summary>
    /// <typeparam name="TEvent">The type of the event (or message) being consumed.</typeparam>
    public interface IRabbitFlowConsumer<TEvent>
    {
        /// <summary>
        /// Handles a RabbitMQ message of the specified event type asynchronously.
        /// This method is called when a message of type <typeparamref name="TEvent"/> is received from the RabbitMQ queue.
        /// </summary>
        /// <param name="message">The message to handle, containing the event data.</param>
        /// <param name="context">The AMQP metadata of the received message, including <c>MessageId</c>, <c>CorrelationId</c>, headers, and delivery information.</param>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        /// <returns>A task representing the asynchronous message handling process.</returns>
        Task HandleAsync(TEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken);
    }
}