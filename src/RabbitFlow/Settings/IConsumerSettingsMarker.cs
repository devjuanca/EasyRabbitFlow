using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EasyRabbitFlow.Settings
{
    internal interface IConsumerSettingsMarker
    {
        Type ConsumerType { get; }
        Type EventType { get; }
        IConsumerSettingsBase SettingsInstance { get; }
        ConsumerSettingsFactory Factory { get; }
    }

    internal sealed class ConsumerSettingsMarker<TConsumer, TEvent> : IConsumerSettingsMarker
        where TConsumer : class
        where TEvent : class
    {
        public ConsumerSettingsMarker(ConsumerSettings<TConsumer> settings, ConsumerSettingsFactory factory)
        {
            SettingsInstance = settings;
            Factory = factory;
        }
        public Type ConsumerType => typeof(TConsumer);
        public Type EventType => typeof(TEvent);
        public IConsumerSettingsBase SettingsInstance { get; }
        public ConsumerSettingsFactory Factory { get; }
    }

    internal sealed class ConsumerSettingsFactory
    {
        public Func<ReadOnlyMemory<byte>, JsonSerializerOptions, object?> Deserialize { get; set; } = default!;
        public Func<object, object, CancellationToken, Task> InvokeHandleAsync { get; set; } = default!;
        public Func<object, string, IServiceProvider, Task>? PublishToCustomDeadletter { get; set; }
    }
}
