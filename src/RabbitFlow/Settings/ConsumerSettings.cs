using Microsoft.Extensions.DependencyInjection;
using RabbitFlow.Services;
using System;
using System.Linq;

namespace RabbitFlow.Settings
{

    public class ConsumerSettings
    {
        private readonly IServiceCollection _services;

        /// <summary>
        /// Gets or sets a value indicating whether messages are automatically acknowledged in case of an error. Defaults to True (Reject and Dispose) 
        /// message will be lost unless a custom dead-letter queue is configured.
        /// If False, message will be rejected and sent to the dead-letter queue (if configured). 
        /// If custom dead-letter queue is also configured message will be sent to both queues.
        /// In no case the message will be requeued.
        /// </summary>
        public bool AutoAckOnError { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether queue, exchange and binding will be automatically generated.
        /// </summary>
        public bool AutoGenerate { get; set; } = false;

        /// <summary>
        /// Gets or sets the number of messages that the consumer can prefetch. Defaults to 1.
        /// </summary>
        public ushort PrefetchCount { get; set; } = 1;

        /// <summary>
        /// Gets or sets the timeout duration for processing a single message. Defaults to 30 seconds.
        /// </summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

        private readonly string _queueName;

        public ConsumerSettings(IServiceCollection services, string queueName)
        {
            _services = services;
            _queueName = queueName;
        }

        /// <summary>
        /// Sets a custom exchange and routing key to send a message if fails. 
        /// If AutoAckOnError is True the message will be sent to both configured dead-letter (by infrastructure) and custom dead-letter.
        /// </summary>
        /// <typeparam name="TConsumer"></typeparam>
        /// <param name="settings"></param>
        public void ConfigureCustomDeadletter<TConsumer>(Action<CustomDeadLetterSettings<TConsumer>> settings) where TConsumer : class
        {
            var customDeasLetter = new CustomDeadLetterSettings<TConsumer>();

            settings.Invoke(customDeasLetter);

            _services.AddSingleton(customDeasLetter);
        }

        /// <summary>
        /// Sets the retry policy for message processing.
        /// </summary>
        /// <param name="settings">A delegate to configure the retry policy.</param>
        public void ConfigureRetryPolicy<TConsumer>(Action<RetryPolicy<TConsumer>> settings) where TConsumer : class
        {
            var retryPolicy = new RetryPolicy<TConsumer>();

            settings.Invoke(retryPolicy);

            _services.AddSingleton(retryPolicy);
        }

        /// <summary>
        /// Configures auto-generation settings for a specific consumer.
        /// </summary>
        /// <typeparam name="TConsumer">Type of the consumer.</typeparam>
        /// <param name="settings">Action to configure auto-generation settings.</param>
        public void ConfigureAutoGenerate<TConsumer>(Action<AutoGenerateSettings<TConsumer>> settings) where TConsumer : class
        {
            var autoGenerateSettings = new AutoGenerateSettings<TConsumer>();

            settings.Invoke(autoGenerateSettings);

            _services.AddSingleton(autoGenerateSettings);
        }



        /// <summary>
        /// Sets the consumer handler implementation for the specified consumer type.
        /// </summary>
        /// <typeparam name="TConsumer">The type of the consumer handler.</typeparam>
        public void SetConsumerHandler<TConsumer>() where TConsumer : class
        {
            var consumerImplementation = typeof(TConsumer);

            var consumerAbstraction = consumerImplementation.GetInterfaces()
                                                            .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IRabbitFlowConsumer<>));

            if (consumerAbstraction != null)
            {
                _services.AddTransient(consumerAbstraction, consumerImplementation);

                var opt = new ConsumerSettings<TConsumer>()
                {
                    AutoAckOnError = AutoAckOnError,
                    AutoGenerate = AutoGenerate,
                    PrefetchCount = PrefetchCount,
                    QueueName = _queueName,
                    Timeout = Timeout
                };

                _services.AddSingleton(opt);
            }
            else
            {
                throw new Exception("Consumer must implement IHvBusConsumer<T>");
            }
        }
    }

    /// <summary>
    /// Represents consumer-specific settings for a RabbitMQ consumer.
    /// </summary>
    /// <typeparam name="TConsumer">The type of the consumer.</typeparam>
    public class ConsumerSettings<TConsumer>
    {
        /// <summary>
        /// Gets or sets a value indicating whether messages are automatically acknowledged after consumption.
        /// </summary>
        public bool AutoAckOnError { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether exchanges and queues should be created.
        /// </summary>
        public bool AutoGenerate { get; set; } = false;

        /// <summary>
        /// Gets or sets the number of messages that the consumer can prefetch.
        /// </summary>
        public ushort PrefetchCount { get; set; } = 1;

        /// <summary>
        /// Gets or sets the name of the queue being consumed.
        /// </summary>
        public string QueueName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the timeout duration for processing a single message.
        /// </summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    ///  Represents temporary-consumer specific settings for a RabbitMQ consumer.
    /// </summary>
    public class TemporaryConsummerSettings
    {
        /// <summary>
        /// Gets or sets the number of messages that the consumer can prefetch.
        /// </summary>
        public ushort PrefetchCount { get; set; } = 1;

        /// <summary>
        /// Gets or sets the timeout duration for processing a single message.
        /// </summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    }
}