using EasyRabbitFlow.Services;
using RabbitFlowFanoutSample.Events;

namespace RabbitFlowFanoutSample.Consumers;

public class VolatileConsumer : IRabbitFlowConsumer<VolatileEvent>
{
    public Task HandleAsync(VolatileEvent message, CancellationToken cancellationToken)
    {
        Console.WriteLine($"New volatile event received. Event:{message.Id}");

        return Task.CompletedTask;
    }
}
