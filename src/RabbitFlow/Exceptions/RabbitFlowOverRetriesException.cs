using System;
using System.Collections.Generic;
using System.Text;

namespace EasyRabbitFlow.Exceptions
{
    /// <summary>
    /// Exception thrown when a message processing operation has exhausted all retry attempts
    /// defined in the retry policy without succeeding.
    /// </summary>
    /// <remarks>
    /// This exception encapsulates the last exception that occurred during the final retry attempt,
    /// preserving the original error details and stack trace as the inner exception.
    /// It is typically used to signal that a message should be moved to a dead-letter queue
    /// after the maximum number of processing attempts has been reached.
    /// </remarks>
    public class RabbitFlowOverRetriesException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RabbitFlowOverRetriesException"/> class.
        /// </summary>
        /// <param name="maxRetries">The maximum number of retries that were attempted before giving up.</param>
        /// <param name="innerException">The exception that caused the final retry to fail, or null if no specific exception was captured.</param>
        public RabbitFlowOverRetriesException(int maxRetries, Exception? innerException) : base($"The maximum number of retries ({maxRetries}) has been exceeded.", innerException)
        {}
    }
}