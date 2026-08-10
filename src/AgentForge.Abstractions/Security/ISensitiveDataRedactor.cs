using AgentForge.Domain.Security;

namespace AgentForge.Abstractions.Security;

public interface ISensitiveDataRedactor
{
    RedactionResult Redact(object? value);
}
