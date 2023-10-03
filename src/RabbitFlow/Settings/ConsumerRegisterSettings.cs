namespace RabbitFlow.Settings
{
    public class ConsumerRegisterSettings
    {
        /// <summary>
        ///  Indicates whether to create a new consumer instance per message. Default is false.
        /// </summary>
        public bool PerMessageInstance { get; set; } = false;

        /// <summary>
        /// Set the consumer as active or not. Default is true.
        /// </summary>
        public bool Active { get; set; } = true;
    }
}
