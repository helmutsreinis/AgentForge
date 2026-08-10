namespace AgentForge.Abstractions.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
