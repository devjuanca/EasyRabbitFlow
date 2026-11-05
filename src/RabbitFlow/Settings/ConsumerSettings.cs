using EasyRabbitFlow.Exceptions;
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

        int? MaxBrokerDeliveries { get; set; }

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
        /// reserved substrings: <c>deadletter</c>, <c>-exchange</c> and <c>-routing-key</c> for the queue
        /// name, and only <c>deadletter</c> for any auto-generate exchange/routing-key names.
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
        /// <remarks>
        /// When <see cref="AutoGenerate"/> is <c>true</c>, this value (together with the retry policy) drives the
        /// queue's <c>x-consumer-timeout</c> argument, so the broker doesn't kill the consumer while it retries in
        /// process. That argument only takes effect when the queue is first created: RabbitMQ silently ignores a
        /// changed <c>x-consumer-timeout</c> when an existing queue is redeclared (no error, no warning), so changing
        /// <see cref="Timeout"/>, <c>MaxRetryCount</c>, or <c>RetryInterval</c> on a queue that already exists does
        /// not update it — delete and recreate the queue (or apply a <c>consumer-timeout</c> policy) to change it.
        /// The per-queue <c>x-consumer-timeout</c> override requires RabbitMQ 3.12+; on older brokers raise
        /// <c>consumer_timeout</c> at the broker level instead.
        /// </remarks>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Maximum number of times the <b>broker</b> may return the same delivery to the queue before the consumer
        /// gives up on it: when a message arrives with <c>x-delivery-count</c> at or above this value, the handler is
        /// not invoked and the message is dead-lettered as a permanent failure (<c>RabbitFlowPoisonMessageException</c>;
        /// the reprocessor parks it). Default is 5.
        /// </summary>
        /// <remarks>
        /// A delivery the broker takes back (consumer acknowledgement timeout, cancelled consumer, closed channel) goes
        /// back to the <i>head</i> of its queue and is delivered again first, so a message that always outlives the
        /// broker timeout blocks the queue until the broker's own <c>delivery-limit</c> (20 on RabbitMQ 4.x quorum
        /// queues) drops it — raw, without the library's envelope. Giving up earlier keeps the queue flowing and lands
        /// the message in the dead-letter queue with its envelope. Only <b>quorum</b> queues carry the header; on
        /// classic queues there is no count, this setting has no effect, and the handler only sees
        /// <c>RabbitFlowMessageContext.Redelivered</c>. The library's own in-process retries never requeue, so they
        /// do not increase the count. <c>null</c> disables the check.
        /// </remarks>
        public int? MaxBrokerDeliveries { get; set; } = 5;

        /// <summary>
        /// Configures the in-process retry policy for the consumer: the handler is invoked again, in the same
        /// delivery and while it stays unacknowledged, for transient failures.
        /// </summary>
        /// <remarks>
        /// Deprecated: prefer <see cref="ConfigureDeadLetterReprocess"/>, which retries from the dead-letter queue on
        /// a persistent counter without holding the delivery unacknowledged (and therefore without stretching the
        /// broker's consumer timeout). Still fully functional; scheduled for removal in the next major version.
        /// </remarks>
        /// <param name="settings">An action to configure the retry policy.</param>
        [Obsolete("In-process retries are deprecated in favour of the dead-letter reprocessor (ConfigureDeadLetterReprocess); they will be removed in the next major version.")]
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
            if (_autoGenerateSettings != null)
            {
                ValidateQueueType(_autoGenerateSettings);
            }

            if (DisableNameValidation && !AutoGenerate)
            {
                return;
            }

            RabbitFlowNameRules.ValidateQueueName(QueueName, "queueName");

            if (_autoGenerateSettings != null)
            {
                RabbitFlowNameRules.ValidateBindingName(_autoGenerateSettings.ExchangeName, nameof(AutoGenerateSettings<TConsumer>.ExchangeName));
                RabbitFlowNameRules.ValidateBindingName(_autoGenerateSettings.RoutingKey, nameof(AutoGenerateSettings<TConsumer>.RoutingKey));
            }
        }

        // Rejects at registration what the broker would reject at declare time, with the reason spelled out: a
        // quorum queue cannot be exclusive or non-durable, and an explicit QueueType must agree with any
        // x-queue-type the caller also put in Args (the broker would otherwise get two opinions).
        private void ValidateQueueType(AutoGenerateSettings<TConsumer> autoGenerate)
        {
            object? rawArg = null;

            var hasArg = autoGenerate.Args != null && autoGenerate.Args.TryGetValue("x-queue-type", out rawArg);

            if (autoGenerate.QueueType.HasValue && hasArg)
            {
                var argType = rawArg?.ToString();

                if (!string.Equals(argType, autoGenerate.QueueType.Value.ToArgument(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new RabbitFlowException(
                        $"[RABBIT-FLOW]: Consumer for queue '{QueueName}' sets QueueType = {autoGenerate.QueueType.Value} but Args[\"x-queue-type\"] = '{argType}'. " +
                        "Use one or make them agree.");
                }
            }

            // The type given through Args counts the same as the typed property.
            var mainType = autoGenerate.QueueType ?? (hasArg ? QueueTypeExtensions.FromArgument(rawArg) : null);

            var deadLetterType = autoGenerate.GenerateDeadletterQueue ? (autoGenerate.DeadLetterQueueType ?? mainType) : null;

            var anyQuorum = mainType == Settings.QueueType.Quorum || deadLetterType == Settings.QueueType.Quorum;

            if (mainType == Settings.QueueType.Quorum && autoGenerate.ExclusiveQueue)
            {
                throw new RabbitFlowException(
                    $"[RABBIT-FLOW]: Consumer for queue '{QueueName}' sets QueueType = Quorum with ExclusiveQueue = true. " +
                    "Quorum queues are replicated and cannot be exclusive to one connection; use QueueType = Classic for exclusive queues.");
            }

            if (anyQuorum && !autoGenerate.DurableQueue)
            {
                throw new RabbitFlowException(
                    $"[RABBIT-FLOW]: Consumer for queue '{QueueName}' requests a quorum queue with DurableQueue = false. " +
                    "Quorum queues are always durable; set DurableQueue = true or use QueueType = Classic.");
            }

            if (anyQuorum && autoGenerate.AutoDeleteQueue)
            {
                throw new RabbitFlowException(
                    $"[RABBIT-FLOW]: Consumer for queue '{QueueName}' requests a quorum queue with AutoDeleteQueue = true. " +
                    "Quorum queues cannot be auto-delete; set AutoDeleteQueue = false or use QueueType = Classic.");
            }
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
