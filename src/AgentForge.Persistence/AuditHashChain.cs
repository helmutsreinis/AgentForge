using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain.Auditing;

namespace AgentForge.Persistence;

internal static class AuditHashChain
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string Compute(AuditEventDraft auditEvent, long sequence, string previousHash)
    {
        var canonical = string.Join('\n',
            auditEvent.EventId.ToString("D"),
            sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            auditEvent.Timestamp.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            auditEvent.InstallationId?.ToString() ?? string.Empty,
            auditEvent.ActorId.Value,
            auditEvent.CorrelationId.Value,
            auditEvent.CausationId?.Value ?? string.Empty,
            auditEvent.OperationType,
            auditEvent.Outcome.ToString(),
            auditEvent.Input.Json,
            auditEvent.Output.Json,
            auditEvent.ErrorClassification ?? string.Empty,
            previousHash);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(digest);
    }
}
