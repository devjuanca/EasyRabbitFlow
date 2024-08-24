namespace EasyRabbitFlow.Settings
{
    public class ConsumerRegisterSettings
    {
        /// <summary>
        /// Gets or sets a value indicating whether a new instance of the consumer should be created for each message.
        /// When set to <c>true</c>, a fresh instance of the consumer will be instantiated for every received message.
        /// This ensures isolated processing of messages, with each instance operating within its own scope.
        /// While this approach can enhance the reliability and independence of message processing, it may also introduce
        /// additional overhead due to the creation of new instances and scopes for each message. The default value is <c>true</c>.
        /// </summary>
        public bool CreateNewInstancePerMessage { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether the consumer is active and capable of processing messages.
        /// When set to <c>true</c>, the consumer will be actively receiving and processing messages from the queue.
        /// If set to <c>false</c>, the consumer will be registered in the system but will not consume any messages,
        /// effectively pausing its operation without removing its configuration. The default value is <c>true</c>.
        /// </summary>
        public bool Active { get; set; } = true;
    }
}
