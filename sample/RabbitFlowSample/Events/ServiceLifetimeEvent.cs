namespace RabbitFlowSample.Events;

public class ServiceLifetimeEvent
{
    public Guid EventId { get; set; } = Guid.NewGuid();
}
