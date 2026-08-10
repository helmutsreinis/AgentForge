using AgentForge.Domain.Auditing;

namespace AgentForge.Domain.Security;

public sealed record RedactionResult(RedactedData Data, int RedactionCount)
{
    public bool ContainsRedactions => RedactionCount > 0;
}
