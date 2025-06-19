namespace RabbitFlowSample.Events;

public class VolatileEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
}
