namespace AgentForge.Abstractions.Tracing;

public interface ICorrelationContext
{
    string CorrelationId { get; }
}
