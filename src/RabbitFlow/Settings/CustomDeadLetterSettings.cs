namespace RabbitFlow.Settings
{

    /// <summary>
    /// Represents the custom dead letter settings to handle messages on failure.
    /// </summary>
    public class CustomDeadLetterSettings<TConsumer>
    {
        public string DeadletterQueueName { get; set; } = string.Empty;
    }
}
