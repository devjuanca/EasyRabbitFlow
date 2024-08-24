using System;

namespace EasyRabbitFlow.Exceptions
{
    /// <summary>
    /// Represents errors that occur during the RabbitMQ flow processing within the EasyRabbitFlow framework.
    /// </summary>
    public class RabbitFlowException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RabbitFlowException"/> class.
        /// </summary>
        public RabbitFlowException() : base() { }


        /// <summary>
        /// Initializes a new instance of the <see cref="RabbitFlowException"/> class with a specified error message.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public RabbitFlowException(string message) : base(message) { }


        /// <summary>
        /// Initializes a new instance of the <see cref="RabbitFlowException"/> class with a specified error message and a reference to the inner exception that is the cause of this exception.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        /// <param name="innerException">The exception that is the cause of the current exception, or a null reference if no inner exception is specified.</param>
        public RabbitFlowException(string message, Exception innerException) : base(message, innerException) { }
    }
}
