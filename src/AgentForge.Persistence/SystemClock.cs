using AgentForge.Abstractions.Time;

namespace AgentForge.Persistence;

internal sealed class SystemClock : IClock, IIdentifierGenerator
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Guid NewGuid() => Guid.NewGuid();
}
