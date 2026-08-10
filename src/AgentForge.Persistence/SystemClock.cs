using AgentForge.Abstractions.Time;

namespace AgentForge.Persistence;

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
