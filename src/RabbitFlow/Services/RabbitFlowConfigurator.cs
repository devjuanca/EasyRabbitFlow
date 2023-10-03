using System;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RabbitFlow.Settings;
using RabbitMQ.Client;

namespace RabbitFlow.Services
{

    /// <summary>
    /// Configures RabbitFlow services and settings.
    /// </summary>
    public class RabbitFlowConfigurator
    {
        private readonly IServiceCollection _services;

        public RabbitFlowConfigurator(IServiceCollection services)
        {
            _services = services;
        }

        /// <summary>
        /// Configures RabbitMQ settings to the configuration.
        /// </summary>
        /// <param name="settings">A delegate to configure RabbitMQ host settings.</param>
        public void ConfigureHost(Action<HostSettings> settings)
        {
            var rabbitHVSettings = new HostSettings();

            settings.Invoke(rabbitHVSettings);

            var factory = new ConnectionFactory()
            {
                HostName = rabbitHVSettings.Host,
                Port = rabbitHVSettings.Port,
                UserName = rabbitHVSettings.Username,
                Password = rabbitHVSettings.Password,
                VirtualHost = rabbitHVSettings.VirtualHost,
                AutomaticRecoveryEnabled = rabbitHVSettings.AutomaticRecoveryEnabled
            };

            _services.AddSingleton(factory);
        }

        /// <summary>
        /// Configures JSON serializer options for message serialization.
        /// </summary>
        /// <param name="settings">A delegate to configure JSON serializer options.</param>
        public void ConfigureJsonSerializerOptions(Action<JsonSerializerOptions> settings)
        {
            var jsonOptions = new JsonSerializerOptions();

            settings.Invoke(jsonOptions);

            _services.AddSingleton(jsonOptions);
        }

        /// <summary>
        /// Configures publisher options for message publishing.
        /// </summary>
        /// <param name="settings">A delegate to configure publisher options.</param>
        public void ConfigurePublisher(Action<PublisherOptions>? settings = null)
        {
            var publisherOptions = new PublisherOptions();

            settings?.Invoke(publisherOptions);

            _services.AddSingleton(publisherOptions);
        }

        /// <summary>
        /// Adds a consumer with specified settings to the configuration.
        /// </summary>
        /// <param name="queueName">The name of the queue to consume from.</param>
        /// <param name="settings">A delegate to configure consumer settings.</param>
        public void AddConsumer(string queueName, Action<ConsumerSettings> settings)
        {
            var consumerSettings = new ConsumerSettings(_services, queueName);

            settings.Invoke(consumerSettings);

            _services.AddSingleton(consumerSettings);
        }
    }
}
