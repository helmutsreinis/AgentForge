using AgentForge.Abstractions.Tracing;

namespace AgentForge.Host.Http;

public sealed class CorrelationContext : ICorrelationContext
{
    private readonly AsyncLocal<string?> _current = new();

    public string CorrelationId => _current.Value ?? string.Empty;

    internal void Set(string? value) => _current.Value = value;
}
