using EasyRabbitFlow.Exceptions;
using EasyRabbitFlow.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using System;
using System.Linq;
using System.Linq.Expressions;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ExchangeType = EasyRabbitFlow.Settings.ExchangeType;

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

        private static readonly ConcurrentDictionary<Type, Func<ReadOnlyMemory<byte>, JsonSerializerOptions, object?>> _deserializeCache = new ConcurrentDictionary<Type, Func<ReadOnlyMemory<byte>, JsonSerializerOptions, object?>>();
    

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

                // EasyRabbitFlow owns recovery itself: the consumer re-establishes its connection and channel and
                // re-declares its full topology on an unexpected shutdown (see ConsumerInstance.TriggerRecoveryAsync).
                // The client's built-in recovery is intentionally left OFF so the two never run at the same time —
                // client topology recovery re-binds/re-consumes a recorded topology that does NOT include the main
                // queue (declared on a throwaway channel), which fails with 404 NOT_FOUND on reconnect. The library's
                // recovery is configured through HostSettings.AutomaticRecoveryEnabled / NetworkRecoveryInterval.
                AutomaticRecoveryEnabled = false,
                TopologyRecoveryEnabled = false,

                RequestedHeartbeat = hostSettings.RequestedHeartbeat
            };

            _services.AddSingleton(factory);

            // Registered so the consumer host can read the library-managed recovery settings.
            _services.AddSingleton(hostSettings);
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
        public void ConfigurePublisher(Action<PublisherConnectionOptions>? settings = null)
        {
            var publisherOptions = new PublisherConnectionOptions();

            settings?.Invoke(publisherOptions);

            _services.AddSingleton(publisherOptions);
        }

        /// <summary>
        /// Declares an application-owned exchange, created at application startup independently of any consumer
        /// registration. Intended for publisher-only services that own an exchange to which external clients
        /// bind their own queues and routing keys. Call it once per exchange the service owns.
        /// <para>
        /// Declaration happens via a dedicated hosted service (no call to <c>UseRabbitFlowConsumers</c> is
        /// required). Declares are idempotent; an exchange that already exists with different settings is
        /// adopted with a warning instead of failing the application start. If the broker is unreachable at
        /// startup, declaration keeps retrying in the background when
        /// <see cref="HostSettings.AutomaticRecoveryEnabled"/> is true.
        /// </para>
        /// </summary>
        /// <param name="exchangeName">Name of the exchange. Must be non-empty and must not contain the reserved
        /// substring <c>deadletter</c>, which is appended by the framework when auto-generating dead-letter topology.</param>
        /// <param name="exchangeType">Routing semantics of the exchange. Default is <see cref="ExchangeType.Direct"/>.</param>
        /// <param name="configure">Optional delegate to tune durability, auto-delete and extra arguments.</param>
        public void DeclareExchange(string exchangeName, ExchangeType exchangeType = ExchangeType.Direct, Action<ExchangeDeclaration>? configure = null)
        {
            if (string.IsNullOrWhiteSpace(exchangeName))
            {
                throw new RabbitFlowException("Exchange name must not be null or empty.");
            }

            // Only the dead-letter suffix is reserved here: "-exchange"/"-routing-key" are natural parts of an
            // exchange name (and redeclaring an auto-generated "{queue}-exchange" with equal settings is a
            // harmless idempotent no-op), but "deadletter" names are framework-owned machinery.
            if (exchangeName.IndexOf(RabbitFlowTopologyNames.DeadLetterSuffix.TrimStart('-'), StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new RabbitFlowException(
                    $"Exchange name '{exchangeName}' contains the reserved substring 'deadletter'. " +
                    "Dead-letter exchanges are auto-generated and owned by the framework; declare them via AutoGenerate on a consumer instead.");
            }

            foreach (var descriptor in _services)
            {
                if (descriptor.ServiceType == typeof(ExchangeDeclaration)
                    && descriptor.ImplementationInstance is ExchangeDeclaration existing
                    && string.Equals(existing.ExchangeName, exchangeName, StringComparison.Ordinal))
                {
                    throw new RabbitFlowException($"Exchange '{exchangeName}' is declared more than once.");
                }
            }

            var declaration = new ExchangeDeclaration(exchangeName, exchangeType);

            configure?.Invoke(declaration);

            _services.AddSingleton(declaration);

            // TryAddEnumerable dedupes by implementation type, so calling DeclareExchange multiple times
            // registers the hosted service exactly once while every ExchangeDeclaration is still resolved.
            _services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, TopologyHostedService>());
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

            consumerSettings.Validate();

			_services.AddSingleton(consumerSettings);

			var consumerType = typeof(TConsumer);
			
            var consumerInterface = consumerType.GetInterfaces().FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IRabbitFlowConsumer<>))
				?? throw new Exception($"Consumer {consumerType.Name} must implement IRabbitFlowConsumer<TEvent>.");
			
            var eventType = consumerInterface.GetGenericArguments().First();

            // Get method info once
            var handleMethod = consumerType.GetMethod(nameof(IRabbitFlowConsumer<object>.HandleAsync))
                ?? throw new InvalidOperationException($"Method HandleAsync not found in {consumerType.Name}.");

            // Build a strongly-typed wrapper via expression once.
            Func<object, object, RabbitFlowMessageContext, CancellationToken, Task> compiledHandleAsync = BuildHandleWrapper(consumerType, eventType, handleMethod);

            var factory = new ConsumerSettingsFactory
            {
                Deserialize = GetOrBuildDeserializer(eventType),
                InvokeHandleAsync = compiledHandleAsync
            };


            // Build generic marker type ConsumerSettingsMarker<TConsumer,TEvent>
            var markerType = typeof(ConsumerSettingsMarker<,>).MakeGenericType(consumerType, eventType);
			
            var markerInstance = Activator.CreateInstance(markerType, consumerSettings, factory)!;
			
            _services.AddSingleton(typeof(IConsumerSettingsMarker), markerInstance);
        }

        // Builds a compiled delegate that wraps TConsumer.HandleAsync(TEvent, RabbitFlowMessageContext, CancellationToken)
        private static Func<object, object, RabbitFlowMessageContext, CancellationToken, Task> BuildHandleWrapper(
            Type consumerType,
            Type eventType,
            System.Reflection.MethodInfo handleMethod)
        {
            var consumerObj = Expression.Parameter(typeof(object), "consumerObj");

            var messageObj = Expression.Parameter(typeof(object), "messageObj");

            var contextParam = Expression.Parameter(typeof(RabbitFlowMessageContext), "context");

            var ct = Expression.Parameter(typeof(CancellationToken), "ct");

            var call = Expression.Call(
                Expression.Convert(consumerObj, consumerType),
                handleMethod,
                Expression.Convert(messageObj, eventType),
                contextParam,
                ct);

            var lambda = Expression.Lambda<Func<object, object, RabbitFlowMessageContext, CancellationToken, Task>>(call, consumerObj, messageObj, contextParam, ct);

            return lambda.Compile();
        }

        // Builds and caches a strongly-typed JsonSerializer.Deserialize<TEvent>(ReadOnlySpan<byte>, JsonSerializerOptions)
        private static Func<ReadOnlyMemory<byte>, JsonSerializerOptions, object?> GetOrBuildDeserializer(Type eventType)
        {
            return _deserializeCache.GetOrAdd(eventType, Build);

            static Func<ReadOnlyMemory<byte>, JsonSerializerOptions, object?> Build(Type t)
            {
                var memParam = Expression.Parameter(typeof(ReadOnlyMemory<byte>), "mem");
                
                var optsParam = Expression.Parameter(typeof(JsonSerializerOptions), "opts");

                // Access mem.Span
                var spanProp = Expression.Property(memParam, nameof(ReadOnlyMemory<byte>.Span));

                // Method: JsonSerializer.Deserialize<T>(ReadOnlySpan<byte>, JsonSerializerOptions)
                var genericMethod = typeof(JsonSerializer)
                    .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    .First(m => m.Name == nameof(JsonSerializer.Deserialize) && m.IsGenericMethod && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(ReadOnlySpan<byte>));

                var closedMethod = genericMethod.MakeGenericMethod(t);

                var call = Expression.Call(closedMethod, spanProp, optsParam);

                // box result
                var body = Expression.Convert(call, typeof(object));

                var lambda = Expression.Lambda<Func<ReadOnlyMemory<byte>, JsonSerializerOptions, object?>>(body, memParam, optsParam);
                
                return lambda.Compile();
            }
        }
    }
}