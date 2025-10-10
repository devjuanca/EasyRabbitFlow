using EasyRabbitFlow.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RabbitMQ.Client;
using System;
using System.Text.Json;

namespace EasyRabbitFlow.Services
{

    /// <summary>
    /// Provides methods to configure RabbitFlow services and settings.
    /// This class is responsible for setting up RabbitMQ connections, 
    /// message serialization, publisher options, and consumer registration within the application.
    /// </summary>
    public class RabbitFlowConfigurator
    {
        private readonly IServiceCollection _services;


        /// <summary>
        /// Initializes a new instance of the <see cref="RabbitFlowConfigurator"/> class.
        /// </summary>
        /// <param name="services">The service collection used to register RabbitFlow services.</param>
        public RabbitFlowConfigurator(IServiceCollection services)
        {
            _services = services;
        }


        /// <summary>
        /// Configures the RabbitMQ host settings for the application.
        /// This method allows you to specify the RabbitMQ server details such as host, port, credentials, and other connection settings.
        /// </summary>
        /// <param name="settings">A delegate to configure the <see cref="HostSettings"/>.</param>
        public void ConfigureHost(Action<HostSettings> settings)
        {
            var hostSettings = new HostSettings();

            settings.Invoke(hostSettings);

            var factory = new ConnectionFactory()
            {
                HostName = hostSettings.Host,
                Port = hostSettings.Port,
                UserName = hostSettings.Username,
                Password = hostSettings.Password,
                VirtualHost = hostSettings.VirtualHost,
                AutomaticRecoveryEnabled = hostSettings.AutomaticRecoveryEnabled,
                TopologyRecoveryEnabled = true,
                NetworkRecoveryInterval = hostSettings.NetworkRecoveryInterval,
                RequestedHeartbeat = hostSettings.RequestedHeartbeat
            };

            _services.AddSingleton(factory);
        }

        /// <summary>
        /// Configures JSON serializer options for message serialization.
        /// This method allows you to customize how messages are serialized and deserialized 
        /// when being sent to or received from RabbitMQ.
        /// </summary>
        /// <param name="settings">A delegate to configure the <see cref="JsonSerializerOptions"/>.</param>
        public void ConfigureJsonSerializerOptions(Action<JsonSerializerOptions> settings)
        {
            var jsonOptions = new JsonSerializerOptions();

            settings.Invoke(jsonOptions);

            _services.TryAddKeyedSingleton("RabbitFlowJsonSerializer", jsonOptions);
        }

        /// <summary>
        /// Configures publisher options for message publishing.
        /// This method allows customization of the behavior of message publishers, such as managing RabbitMQ connections.
        /// </summary>
        public void ConfigurePublisher(Action<PublisherOptions>? settings = null)
        {
            var publisherOptions = new PublisherOptions();

            settings?.Invoke(publisherOptions);

            _services.AddSingleton(publisherOptions);
        }

        /// <summary>
        /// Adds a consumer to the configuration with specified settings.
        /// This method registers a RabbitMQ consumer that listens to a specified queue, 
        /// with the ability to configure consumer-specific settings such as retry policies and queue management.
        /// </summary>
        /// <typeparam name="TConsumer">The type of the consumer to register, which must implement the <see cref="IRabbitFlowConsumer{T}"/> interface.</typeparam>
        /// <param name="queueName">The name of the RabbitMQ queue to consume messages from.</param>
        /// <param name="settings">A delegate to configure the <see cref="ConsumerSettings{TConsumer}"/>.</param>
        public void AddConsumer<TConsumer>(string queueName, Action<ConsumerSettings<TConsumer>> settings) where TConsumer : class
        {
            var consumerSettings = new ConsumerSettings<TConsumer>(_services, queueName);

            settings.Invoke(consumerSettings);

            _services.AddSingleton(consumerSettings);
        }
    }
}