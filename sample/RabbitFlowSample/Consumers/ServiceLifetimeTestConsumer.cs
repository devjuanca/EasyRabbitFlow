using EasyRabbitFlow.Services;
using RabbitFlowSample.Events;

namespace RabbitFlowSample.Consumers;

public class ServiceLifetimeTestConsumer(
    GuidSingletonService guidSingletonService,
    GuidScopedService guidScopedService,
    GuidTransientService guidTransientService,
    ILogger<ServiceLifetimeTestConsumer> logger) : IRabbitFlowConsumer<ServiceLifetimeEvent>
{
    public async Task HandleAsync(ServiceLifetimeEvent message, CancellationToken cancellationToken)
    {
        var singletonId = guidSingletonService.Guid;

        var scopedId = guidScopedService.Guid;

        var transientId = guidTransientService.Guid;

        logger.LogInformation("ServiceLifetimeTestConsumer received message: {Message}, Singleton ID: {SingletonId}, Scoped ID: {ScopedId}, Transient ID: {TransientId}",
            message, singletonId, scopedId, transientId);

        await Task.Delay(1000, cancellationToken);
    }
}
