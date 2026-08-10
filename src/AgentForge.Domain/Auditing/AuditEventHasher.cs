using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentForge.Domain.Auditing;

public static class AuditEventHasher
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string Compute(AuditEventDraft auditEvent, long sequence, string previousHash)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousHash);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, auditEvent.EventId.ToString("D"));
        Append(hash, sequence.ToString(CultureInfo.InvariantCulture));
        Append(hash, auditEvent.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Append(hash, auditEvent.InstallationId?.ToString() ?? string.Empty);
        Append(hash, auditEvent.ActorId.Value);
        Append(hash, auditEvent.CorrelationId.Value);
        Append(hash, auditEvent.CausationId?.Value ?? string.Empty);
        Append(hash, auditEvent.OperationType);
        Append(hash, auditEvent.Outcome.ToString());
        Append(hash, auditEvent.Input.Json);
        Append(hash, auditEvent.Output.Json);
        Append(hash, auditEvent.ErrorClassification ?? string.Empty);
        Append(hash, previousHash);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
