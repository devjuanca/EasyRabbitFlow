using System.Threading;
using System.Threading.Tasks;

namespace RabbitFlow.Services
{

    /// <summary>
    /// Represents a consumer for handling RabbitMQ messages of a specific event type.
    /// </summary>
    /// <typeparam name="TEvent">The type of the event being consumed.</typeparam>
    public interface IRabbitFlowConsumer<TEvent>
    {
        /// <summary>
        /// Handles a RabbitMQ message of the specified event type asynchronously.
        /// </summary>
        /// <param name="message">The message to handle.</param>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        /// <returns>A task representing the asynchronous message handling process.</returns>
        /// <remarks>
        /// Implement this method to define the logic for processing RabbitMQ messages of the specified event type.
        /// The <paramref name="cancellationToken"/> can be used to cancel the operation if needed.
        /// </remarks>
        Task HandleAsync(TEvent message, CancellationToken cancellationToken);
    }
}


