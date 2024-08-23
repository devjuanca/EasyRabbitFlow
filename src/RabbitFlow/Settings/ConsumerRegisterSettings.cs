namespace RabbitFlow.Settings
{
    public class ConsumerRegisterSettings
    {
        /// <summary>
        /// Gets or sets a value indicating whether a new consumer instance should be created for each message.
        /// When set to <c>true</c>, a fresh instance of the consumer will be created for every message received.
        /// This can be useful for ensuring isolated processing but may have performance implications. The default value is <c>true</c>.
        /// </summary>
        public bool PerMessageInstance { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether the consumer is active.
        /// An active consumer will be able to receive and process messages. 
        /// If set to <c>false</c>, the consumer will be registered but not actively consume messages. The default value is <c>true</c>.
        /// </summary>
        public bool Active { get; set; } = true;
    }
}
