namespace EasyRabbitFlow.Settings
{
    public class ConsumerRegisterSettings
    {
        /// <summary>
        /// Gets or sets a value indicating whether the consumer is active and capable of processing messages.
        /// When set to <c>true</c>, the consumer will be actively receiving and processing messages from the queue.
        /// If set to <c>false</c>, the consumer will be registered in the system but will not consume any messages,
        /// effectively pausing its operation without removing its configuration. The default value is <c>true</c>.
        /// </summary>
        public bool Active { get; set; } = true;
    }
}
