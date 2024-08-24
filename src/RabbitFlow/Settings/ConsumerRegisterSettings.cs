namespace RabbitFlow.Settings
{
    public class ConsumerRegisterSettings
    {
        /// <summary>
        /// Gets or sets a value indicating whether the consumer is active.
        /// An active consumer will be able to receive and process messages. 
        /// If set to <c>false</c>, the consumer will be registered but not actively consume messages. The default value is <c>true</c>.
        /// </summary>
        public bool Active { get; set; } = true;
    }
}
