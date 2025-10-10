using EasyRabbitFlow.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;

namespace EasyRabbitFlow.Settings
{
    /// <summary>
    /// Represents consumer-specific settings for a RabbitMQ consumer.
    /// This class provides configuration options such as queue name, 
    /// auto-acknowledgment behavior, message prefetch count, processing timeout, 
    /// retry policy, and more for a specific consumer type.
    /// </summary>
    /// <typeparam name="TConsumer">The type of the consumer that must implement IRabbitFlowConsumer&lt;T&gt;.</typeparam>
    public class ConsumerSettings<TConsumer> where TConsumer : class
    {
        private readonly IServiceCollection _services;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConsumerSettings{TConsumer}"/> class.
        /// Registers the consumer implementation in the dependency injection container.
        /// </summary>
        /// <param name="services">The service collection to which the consumer is registered.</param>
        /// <param name="queueName">The name of the queue being consumed.</param>
        /// <exception cref="Exception">Thrown if the consumer does not implement IRabbitFlowConsumer&lt;T&gt;.</exception>
        public ConsumerSettings(IServiceCollection services, string queueName)
        {
            _services = services;

            QueueName = queueName;

            var consumerImplementation = typeof(TConsumer);

            var consumerAbstraction = consumerImplementation.GetInterfaces()
                                                            .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IRabbitFlowConsumer<>));

            if (consumerAbstraction != null)
            {
                _services.AddTransient<TConsumer>();
            }
            else
            {
                throw new Exception("Consumer must implement IRabbitFlowConsumer<T>");
            }
        }

        /// <summary>
        /// Gets or sets the name of the queue being consumed.
        /// </summary>
        public string QueueName { get; private set; } = string.Empty;

        /// <summary>
        /// Gets or sets the unique identifier for the consumer, which is used to identify the connection associated with the consumer.
        /// If this property is not provided, the <see cref="QueueName"/> will be used as a fallback for identification purposes.
        /// </summary>
        public string? ConsumerId { get; set; }


        /// <summary>
        /// Gets or sets a value indicating whether messages are automatically acknowledged after consumption in case of an error. Default is false.
        /// </summary>
        public bool AutoAckOnError { get; set; } = false;

        /// <summary>
        /// Gets or sets a value indicating whether to automatically generate necessary queue and exchange configurations. Default is false.
        /// </summary>
        public bool AutoGenerate { get; set; } = false;

        /// <summary>
        ///  Gets or sets a value indicating whether to extend the dead-letter message with exception details. Default is false.
        /// </summary>
        public bool ExtendDeadletterMessage { get; set; } = false;

        /// <summary>
        /// Gets or sets the number of messages that the consumer can prefetch.
        /// Prefetch count determines how many messages the consumer can hold in memory before processing them. Default is 1.
        /// </summary>
        public ushort PrefetchCount { get; set; } = 1;


        /// <summary>
        /// Gets or sets the timeout duration for processing a single message. Default is 30 seconds.
        /// </summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);


        /// <summary>
        /// Configures the retry policy for the consumer.
        /// The retry policy dictates how the system should handle message consumption failures.
        /// </summary>
        /// <param name="settings">An action to configure the retry policy.</param>
        public void ConfigureRetryPolicy(Action<RetryPolicy<TConsumer>> settings)
        {
            var retryPolicy = new RetryPolicy<TConsumer>();

            settings.Invoke(retryPolicy);

            _services.AddSingleton(retryPolicy);
        }


        /// <summary>
        /// Configures automatic generation settings for queues and exchanges.
        /// This configuration is applied when the AutoGenerate property is set to true.
        /// </summary>
        /// <param name="settings">An action to configure automatic generation settings.</param>
        public void ConfigureAutoGenerate(Action<AutoGenerateSettings<TConsumer>> settings)
        {
            var autoGenerateSettings = new AutoGenerateSettings<TConsumer>();

            settings.Invoke(autoGenerateSettings);

            _services.AddSingleton(autoGenerateSettings);
        }


        /// <summary>
        /// Configures custom dead-letter settings for the consumer.
        /// Dead-letter settings allow customization of how messages are handled when they cannot be processed successfully.
        /// </summary>
        /// <param name="settings">An action to configure custom dead-letter settings.</param>
        public void ConfigureCustomDeadletter(Action<CustomDeadLetterSettings<TConsumer>> settings)
        {
            var customDeasLetter = new CustomDeadLetterSettings<TConsumer>();

            settings.Invoke(customDeasLetter);

            _services.AddSingleton(customDeasLetter);
        }
    }
}