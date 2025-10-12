using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace EasyRabbitFlow
{
    public static class DependencyInyection
    {
        /// <summary>
        /// Configures core RabbitFlow services using the provided configurator.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> where RabbitFlow dependencies are registered.</param>
        /// <param name="configurator">
        /// An optional delegate for customizing RabbitFlow configuration, such as connection settings or feature enablement.
        /// </param>
        /// <returns>The same <see cref="IServiceCollection"/> instance for fluent chaining.</returns>
        /// <remarks>
        /// Call this method to register the essential RabbitFlow components such as:
        /// <list type="bullet">
        ///   <item><description><see cref="IRabbitFlowPublisher"/> for publishing messages.</description></item>
        ///   <item><description><see cref="IRabbitFlowTemporary"/> for temporary queue handling.</description></item>
        ///   <item><description><see cref="IRabbitFlowPurger"/> for queue purging.</description></item>
        ///   <item><description><see cref="IRabbitFlowState"/> for internal flow management.</description></item>
        /// </list>
        /// Use this when you need RabbitFlow features like publishing or purging without consumer auto-registration.
        /// </remarks>
        public static IServiceCollection AddRabbitFlow(this IServiceCollection services, Action<RabbitFlowConfigurator> configurator = default!)
        {
            configurator ??= _ => { };

            var settings = new RabbitFlowConfigurator(services);

            configurator.Invoke(settings);

            services.AddSingleton<IRabbitFlowPublisher, RabbitFlowPublisher>();

            services.AddSingleton<IRabbitFlowState, RabbitFlowState>();

            services.AddSingleton<IRabbitFlowTemporary, RabbitFlowTemporary>();

            services.AddSingleton<IRabbitFlowPurger, RabbitFlowPurger>();

            return services;
        }

        /// <summary>
        /// Registers the <see cref="ConsumerHostedService"/> in the dependency injection container.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> used to configure dependency injection.</param>
        /// <returns>The same <see cref="IServiceCollection"/> instance for fluent chaining.</returns>
        /// <remarks>
        /// This service automatically discovers and initializes all consumers registered via 
        /// <see cref="ConsumerSettings{T}"/> when the application starts.
        /// <para>
        /// Use this method only when your application is responsible for consuming messages from RabbitMQ.
        /// For publishing-only scenarios, you can omit this registration.
        /// </para>
        /// </remarks>
        public static IServiceCollection UseRabbitFlowConsumers(this IServiceCollection services)
        {
            services.AddHostedService<ConsumerHostedService>();

            return services;
        }
    }
}