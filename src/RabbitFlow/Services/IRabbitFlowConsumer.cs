using System.Threading;
using System.Threading.Tasks;

namespace RabbitFlow.Services
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
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        /// <returns>A task representing the asynchronous message handling process.</returns>
        /// <remarks>
        /// Implement this method to define the logic for processing RabbitMQ messages of the specified event type.
        /// The <paramref name="cancellationToken"/> can be used to cancel the operation if needed, such as when shutting down.
        /// Ensure to handle exceptions and potentially implement retry logic within this method to manage message processing failures.
        /// </remarks>
        Task HandleAsync(TEvent message, CancellationToken cancellationToken);
    }
}