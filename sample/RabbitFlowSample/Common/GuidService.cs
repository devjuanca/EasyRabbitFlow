namespace RabbitFlowSample.Common;

// Demo services used by the WhatsApp consumer to verify that EasyRabbitFlow honors
// the DI lifetime of every dependency it resolves per-message:
//   - GuidSingletonService → one Guid for the lifetime of the app.
//   - GuidScopedService    → a new Guid per consumed message (one DI scope per message).
//   - GuidTransientService → a new Guid every time it is resolved within the same scope.

public sealed class GuidSingletonService
{
    public Guid Guid { get; } = Guid.NewGuid();
}

public sealed class GuidScopedService
{
    public Guid Guid { get; } = Guid.NewGuid();
}

public sealed class GuidTransientService
{
    public Guid Guid { get; } = Guid.NewGuid();
}
