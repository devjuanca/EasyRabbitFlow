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
    internal interface IConsumerSettingsBase
    {
        bool Enable { get; set; }

        string QueueName { get; }

        string? ConsumerId { get; set; }

        bool AutoAckOnError { get; set; }

        bool AutoGenerate { get; set; }

        bool DisableNameValidation { get; set; }

        bool ExtendDeadletterMessage { get; set; }

        bool UnwrapDeadLetterEnvelopes { get; set; }

        ushort PrefetchCount { get; set; }

        TimeSpan Timeout { get; set; }
    }

    public class ConsumerSettings<TConsumer> : IConsumerSettingsBase where TConsumer : class
    {
        private readonly IServiceCollection _services;

        private AutoGenerateSettings<TConsumer>? _autoGenerateSettings;

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
        /// Gets or sets a value indicating whether the consumer is enabled or not.
        /// Default is true.
        /// </summary>
        public bool Enable { get; set; } = true;

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
        /// Gets or sets a value indicating whether messages are automatically acknowledged after consumption in case of an error. 
        /// Default is false.
        /// </summary>
        public bool AutoAckOnError { get; set; } = false;

        /// <summary>
        /// Gets or sets a value indicating whether to automatically generate necessary queue and exchange configurations.
        /// Default is false.
        /// </summary>
        public bool AutoGenerate { get; set; } = false;

        /// <summary>
        /// Gets or sets a value indicating whether to skip <c>RabbitFlowNameRules</c> validation against
        /// reserved substrings (e.g. <c>deadletter</c>, <c>-exchange</c>, <c>-routing-key</c>) for the
        /// queue name and any auto-generate exchange/routing-key names.
        /// </summary>
        /// <remarks>
        /// Only takes effect when <see cref="AutoGenerate"/> is <c>false</c>. When the framework owns
        /// topology generation it appends those reserved substrings to user-supplied names to derive
        /// dead-letter queues, exchanges, and routing keys, so the validation must run regardless of
        /// this flag. Setting this to <c>true</c> while <see cref="AutoGenerate"/> is also <c>true</c>
        /// is silently ignored. Default is <c>false</c>.
        /// </remarks>
        public bool DisableNameValidation { get; set; } = false;

        /// <summary>
        ///  Gets or sets a value indicating whether to extend the dead-letter message with exception details.
        ///  Default is true.
        /// </summary>
        public bool ExtendDeadletterMessage { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether the consumer should defensively detect and unwrap a
        /// <see cref="DeadLetterEnvelope"/> that arrives on the main queue (e.g. because a client manually
        /// copied a message from the dead-letter queue back to the main queue, bypassing the
        /// <see cref="DeadLetterReprocessSettings{TConsumer}">reprocessor</see>).
        /// </summary>
        /// <remarks>
        /// Default is <c>false</c>. When <c>true</c>, every inbound message is fingerprinted with a cheap
        /// byte-level scan; only when the fingerprint matches is the body parsed as a <c>DeadLetterEnvelope</c>
        /// and the inner payload extracted. Prefer the dead-letter reprocessor over manual replay; this flag
        /// only exists as a safety net for environments where manual replay can occur.
        /// </remarks>
        public bool UnwrapDeadLetterEnvelopes { get; set; } = false;

        /// <summary>
        /// Gets or sets the number of messages that the consumer can prefetch.
        /// Prefetch count determines how many messages the consumer can hold in memory before processing them. 
        /// Default is 1.
        /// </summary>
        public ushort PrefetchCount { get; set; } = 1;


        /// <summary>
        /// Gets or sets the timeout duration for processing a single message. 
        /// Default is 30 seconds.
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

            _autoGenerateSettings = autoGenerateSettings;
        }

        // Validates queue name and any configured AutoGenerate names against RabbitFlowNameRules.
        // Skipped only when DisableNameValidation is true AND AutoGenerate is false (the user fully
        // owns the topology and reserved-substring suffixes the framework would append never apply).
        internal void Validate()
        {
            if (DisableNameValidation && !AutoGenerate)
            {
                return;
            }

            RabbitFlowNameRules.Validate(QueueName, "queueName");

            if (_autoGenerateSettings != null)
            {
                RabbitFlowNameRules.Validate(_autoGenerateSettings.ExchangeName, nameof(AutoGenerateSettings<TConsumer>.ExchangeName));
                RabbitFlowNameRules.Validate(_autoGenerateSettings.RoutingKey, nameof(AutoGenerateSettings<TConsumer>.RoutingKey));
            }
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

        /// <summary>
        /// Configures the dead-letter reprocessor for the consumer.
        /// When enabled, a background service periodically drains the consumer's auto-generated dead-letter queue
        /// and re-publishes messages to the main queue until <see cref="DeadLetterReprocessSettings{TConsumer}.MaxReprocessAttempts"/> is reached.
        /// </summary>
        /// <remarks>
        /// Requires <see cref="AutoGenerate"/> to be <c>true</c>; otherwise the reprocessor is ignored.
        /// When the reprocessor is active, <see cref="ExtendDeadletterMessage"/> is forced to <c>true</c> because the reprocessor
        /// relies on the dead-letter envelope wrapping to track attempt counts and recover the original payload.
        /// </remarks>
        /// <param name="settings">An action to configure the reprocessor settings.</param>
        public void ConfigureDeadLetterReprocess(Action<DeadLetterReprocessSettings<TConsumer>> settings)
        {
            var reprocessSettings = new DeadLetterReprocessSettings<TConsumer>();

            settings.Invoke(reprocessSettings);

            _services.AddSingleton(reprocessSettings);
        }
    }
}