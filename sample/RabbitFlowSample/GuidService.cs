namespace RabbitFlowSample;

public class GuidSingletonService
{
    public Guid Guid { get; set; } = Guid.NewGuid();

}

public class GuidScopedService
{
    public Guid Guid { get; set; } = Guid.NewGuid();
}

public class GuidTransientService
{
    public Guid Guid { get; set; } = Guid.NewGuid();
}