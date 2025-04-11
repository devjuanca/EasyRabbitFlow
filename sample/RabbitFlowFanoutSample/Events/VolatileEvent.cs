namespace RabbitFlowFanoutSample.Events;

public class VolatileEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
}
