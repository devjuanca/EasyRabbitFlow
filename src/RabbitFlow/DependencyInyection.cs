using EasyRabbitFlow.Services;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace EasyRabbitFlow
{
    public static class DependencyInyection
    {
        /// <summary>
        /// Configures RabbitFlow services using the specified configurator.
        /// </summary>
        /// <param name="services">The service collection to configure.</param>
        /// <param name="configurator">An optional configurator delegate for fine-tuning RabbitFlow settings.</param>
        /// <returns>The modified service collection.</returns>
        /// <remarks>
        /// Use this method to configure RabbitFlow services within the provided <paramref name="services"/> collection.
        /// The optional <paramref name="configurator"/> delegate allows further customization of RabbitFlow settings.
        /// </remarks>
        public static IServiceCollection AddRabbitFlow(this IServiceCollection services, Action<RabbitFlowConfigurator> configurator = default!)
        {
            configurator ??= _ => { };

            var settings = new RabbitFlowConfigurator(services);

            configurator.Invoke(settings);

            services.AddSingleton<IRabbitFlowPublisher, RabbitFlowPublisher>();

            services.AddSingleton<IRabbitFlowState, RabbitFlowState>();

            services.AddSingleton<IRabbitFlowTemporary, RabbitFlowTemporary>();


            return services;
        }
    }
}