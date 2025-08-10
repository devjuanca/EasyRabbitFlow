using System;

namespace EasyRabbitFlow.Exceptions
{
    /// <summary>
    /// Represents transient errors that occur during the RabbitMQ flow processing within the EasyRabbitFlow framework.
    /// This exception type indicates that the error is temporary and may succeed if retried.
    /// </summary>
    public class RabbitFlowTransientException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RabbitFlowTransientException"/> class.
        /// </summary>
        public RabbitFlowTransientException() : base() { }

        /// <summary>
        /// Initializes a new instance of the <see cref="RabbitFlowTransientException"/> class with a specified error message.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public RabbitFlowTransientException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="RabbitFlowTransientException"/> class with a specified error message and a reference to the inner exception that is the cause of this exception.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        /// <param name="innerException">The exception that is the cause of the current exception, or a null reference if no inner exception is specified.</param>
        public RabbitFlowTransientException(string message, Exception innerException) : base(message, innerException) { }
    }
}
