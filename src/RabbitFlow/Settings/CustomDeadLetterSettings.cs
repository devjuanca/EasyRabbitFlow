namespace EasyRabbitFlow.Settings
{

    /// <summary>
    /// Represents the custom dead-letter settings for handling messages that cannot be processed successfully.
    /// This class allows the configuration of a specific dead-letter queue to which messages will be routed 
    /// when they fail to be consumed by the specified consumer.
    /// </summary>
    public class CustomDeadLetterSettings<TConsumer>
    {
        /// <summary>
        /// Gets or sets the name of the dead-letter queue.
        /// This queue will receive messages that could not be successfully processed by the consumer. 
        /// The default value is an empty string, indicating no dead-letter queue is configured by default.
        /// </summary>
        public string DeadletterQueueName { get; set; } = string.Empty;
    }
}
