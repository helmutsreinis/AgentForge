using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Devices;

public readonly record struct DecoderProposalId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public enum DecoderFieldEncoding { ByteUnsigned, UInt16LittleEndian, UInt16BigEndian, Int16LittleEndian, Int16BigEndian, Ascii, Bytes }

public enum DecoderAuthority
{
    ProtocolDecode,
    DeviceCapture,
    DeviceRead,
    DeviceWrite,
    DeviceFirmware,
    FileSystem,
    ExternalNetwork,
}

public sealed record DecoderFieldDefinition(
    string Name,
    int Offset,
    int Length,
    DecoderFieldEncoding Encoding);

public sealed record DeclarativeDecoderDefinition(
    string DecoderId,
    string Version,
    int FrameLength,
    ImmutableArray<byte> SyncPrefix,
    ImmutableArray<DecoderFieldDefinition> Fields,
    ImmutableSortedSet<DecoderAuthority> Permissions,
    string DefinitionHash)
{
    public bool IsValid()
    {
        if (!SerialDeviceRecordValidator.Text(DecoderId, 128) || !IsVersion(Version) ||
            FrameLength is < 1 or > 4096 || SyncPrefix.Length is < 1 or > 32 || SyncPrefix.Length > FrameLength ||
            Fields.Length > 128 || Fields.Any(field => !ValidField(field)) ||
            Fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != Fields.Length ||
            Permissions.Count is < 1 or > 8 || Permissions.Any(permission => permission != DecoderAuthority.ProtocolDecode) ||
            !SerialDeviceRecordValidator.IsSha256(DefinitionHash)) return false;
        var claimed = new bool[FrameLength];
        for (var index = 0; index < SyncPrefix.Length; index++) claimed[index] = true;
        foreach (var field in Fields)
            for (var index = field.Offset; index < field.Offset + field.Length; index++)
                if (claimed[index]) return false; else claimed[index] = true;
        return string.Equals(DefinitionHash, CalculateHash(this), StringComparison.Ordinal);
    }

    public static string CalculateHash(DeclarativeDecoderDefinition definition)
    {
        var builder = new StringBuilder();
        builder.Append(definition.DecoderId).Append('\n').Append(definition.Version).Append('\n')
            .Append(definition.FrameLength).Append('\n').Append(Convert.ToHexStringLower(definition.SyncPrefix.AsSpan())).Append('\n');
        foreach (var field in definition.Fields.OrderBy(field => field.Offset).ThenBy(field => field.Name, StringComparer.Ordinal))
            builder.Append(field.Name).Append('|').Append(field.Offset).Append('|').Append(field.Length).Append('|').Append(field.Encoding).Append('\n');
        foreach (var permission in definition.Permissions) builder.Append(permission).Append('\n');
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))}";
    }

    private bool ValidField(DecoderFieldDefinition field) =>
        SerialDeviceRecordValidator.Text(field.Name, 64) && field.Offset >= SyncPrefix.Length && field.Length >= 1 &&
        field.Offset + field.Length <= FrameLength && field.Encoding switch
        {
            DecoderFieldEncoding.ByteUnsigned => field.Length == 1,
            DecoderFieldEncoding.UInt16LittleEndian or DecoderFieldEncoding.UInt16BigEndian or
                DecoderFieldEncoding.Int16LittleEndian or DecoderFieldEncoding.Int16BigEndian => field.Length == 2,
            DecoderFieldEncoding.Ascii or DecoderFieldEncoding.Bytes => field.Length <= 256,
            _ => false,
        };

    private static bool IsVersion(string value) => value.Split('.') is [var major, var minor, var patch] &&
        uint.TryParse(major, out _) && uint.TryParse(minor, out _) && uint.TryParse(patch, out _);
}

public sealed record UnknownByteSegment(int Offset, ImmutableArray<byte> Bytes);

public sealed record DecodedSerialFrame(
    int Offset,
    ImmutableSortedDictionary<string, string> Fields,
    ImmutableArray<UnknownByteSegment> UnknownSegments,
    string RawHash);

public sealed record DecoderParseResult(
    ImmutableArray<DecodedSerialFrame> Frames,
    ImmutableArray<UnknownByteSegment> UnframedSegments,
    int InputLength,
    int OperationCount);

public sealed record DecoderEvaluationCase(
    string Name,
    ImmutableArray<byte> Input,
    int ExpectedFrameCount,
    int MinimumUnknownBytes);

public sealed record DecoderEvaluationSuite(
    ImmutableArray<DecoderEvaluationCase> TargetCases,
    ImmutableArray<DecoderEvaluationCase> HoldoutCases,
    int FuzzCases,
    int MaximumOperationsPerByte,
    string SuiteHash);

public static class DecoderEvaluationSuiteHasher
{
    public static string Calculate(DecoderEvaluationSuite suite)
    {
        var builder = new StringBuilder();
        Append(builder, "target", suite.TargetCases);
        Append(builder, "holdout", suite.HoldoutCases);
        builder.Append(suite.FuzzCases).Append('|').Append(suite.MaximumOperationsPerByte);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))}";
    }

    private static void Append(StringBuilder builder, string set, IEnumerable<DecoderEvaluationCase> cases)
    {
        foreach (var item in cases)
            builder.Append(set).Append('|').Append(item.Name).Append('|')
                .Append(Convert.ToHexStringLower(item.Input.AsSpan())).Append('|')
                .Append(item.ExpectedFrameCount).Append('|').Append(item.MinimumUnknownBytes).Append('\n');
    }
}

public sealed record DecoderEvaluationEvidence(
    string CandidateHash,
    string SuiteHash,
    int TargetCases,
    int HoldoutCases,
    int FuzzCases,
    bool TargetPassed,
    bool HoldoutPassed,
    bool MalformedPassed,
    bool PartialPassed,
    bool ConcatenatedPassed,
    bool ResynchronizationPassed,
    bool UnknownFieldsPreserved,
    bool PerformancePassed,
    bool Passed,
    string EvidenceHash);

public sealed record DecoderCanaryEvidence(
    string ScopeId,
    int SampleCount,
    int FailureCount,
    bool RegressionDetected,
    string EvidenceHash)
{
    public bool Passed => SerialDeviceRecordValidator.Text(ScopeId, 256) && SampleCount > 0 &&
        FailureCount == 0 && !RegressionDetected && SerialDeviceRecordValidator.IsSha256(EvidenceHash);
}

public enum DecoderProposalState { Proposed, AwaitingApproval, Rejected, Canary, Active, Quarantined, RolledBack, Archived }

public sealed record DecoderProposalSnapshot(
    DecoderProposalId Id,
    InstallationId InstallationId,
    DeclarativeDecoderDefinition Candidate,
    string? BaselineHash,
    DecoderProposalState State,
    DecoderEvaluationEvidence? Evaluation,
    DecoderCanaryEvidence? Canary,
    ActorId ProposerId,
    ActorId? ApproverId,
    DateTimeOffset UpdatedAtUtc,
    long Version,
    string PreviousSnapshotHash,
    string SnapshotHash)
{
    public bool IsConsistent() => Id.Value != Guid.Empty && InstallationId.Value != Guid.Empty && Candidate.IsValid() &&
        (BaselineHash is null || SerialDeviceRecordValidator.IsSha256(BaselineHash)) &&
        SerialDeviceRecordValidator.Text(ProposerId.Value, 256) && UpdatedAtUtc.Offset == TimeSpan.Zero && Version >= 0 &&
        (Version == 0 ? PreviousSnapshotHash == new string('0', 64) : SerialDeviceRecordValidator.IsSha256($"sha256:{PreviousSnapshotHash}")) &&
        SerialDeviceRecordValidator.IsSha256($"sha256:{SnapshotHash}") && SnapshotHash == CalculateSnapshotHash(this) &&
        State switch
        {
            DecoderProposalState.Proposed => Evaluation is null && Canary is null && ApproverId is null,
            DecoderProposalState.AwaitingApproval => Evaluation is { Passed: true } && Canary is null && ApproverId is null,
            DecoderProposalState.Rejected => Evaluation is { Passed: false } && Canary is null,
            DecoderProposalState.Canary => Evaluation is { Passed: true } && ApproverId is not null && Canary is null,
            DecoderProposalState.Active => Evaluation is { Passed: true } && ApproverId is not null && Canary is { Passed: true },
            DecoderProposalState.Quarantined => Evaluation is not null,
            DecoderProposalState.RolledBack => Evaluation is { Passed: true } && ApproverId is not null,
            DecoderProposalState.Archived => true,
            _ => false,
        };

    public static string CalculateSnapshotHash(DecoderProposalSnapshot snapshot)
    {
        var value = string.Join('\n', snapshot.Id.Value, snapshot.InstallationId.Value, snapshot.Candidate.DefinitionHash,
            snapshot.BaselineHash ?? string.Empty, snapshot.State, snapshot.Evaluation?.EvidenceHash ?? string.Empty,
            snapshot.Canary?.EvidenceHash ?? string.Empty, snapshot.ProposerId.Value, snapshot.ApproverId?.Value ?? string.Empty,
            snapshot.UpdatedAtUtc.UtcTicks, snapshot.Version, snapshot.PreviousSnapshotHash);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

public static class DecoderProposalStateMachine
{
    public static DecoderProposalSnapshot Propose(
        DecoderProposalId id, InstallationId installationId, DeclarativeDecoderDefinition candidate,
        string? baselineHash, ActorId proposer, DateTimeOffset now)
    {
        if (!candidate.IsValid()) throw new InvalidOperationException("Decoder candidate is invalid or requests unrelated authority.");
        return Create(new(id, installationId, candidate, baselineHash, DecoderProposalState.Proposed, null, null,
            proposer, null, now, 0, new string('0', 64), string.Empty));
    }

    public static DecoderProposalSnapshot Evaluate(
        DecoderProposalSnapshot current, DecoderEvaluationEvidence evidence, DateTimeOffset now) =>
        Next(current, evidence.Passed ? DecoderProposalState.AwaitingApproval : DecoderProposalState.Rejected,
            now, evaluation: evidence);

    public static DecoderProposalSnapshot Approve(DecoderProposalSnapshot current, ActorId approver, DateTimeOffset now)
    {
        if (current.State != DecoderProposalState.AwaitingApproval || approver == current.ProposerId)
            throw new InvalidOperationException("Decoder approval requires a separate actor after passing evaluation.");
        return Next(current, DecoderProposalState.Canary, now, approver: approver);
    }

    public static DecoderProposalSnapshot Promote(
        DecoderProposalSnapshot current, DecoderCanaryEvidence canary, DateTimeOffset now)
    {
        if (current.State != DecoderProposalState.Canary || !canary.Passed)
            throw new InvalidOperationException("Decoder promotion requires a passing bounded canary.");
        return Next(current, DecoderProposalState.Active, now, canary: canary);
    }

    public static DecoderProposalSnapshot RecordCanary(
        DecoderProposalSnapshot current, DecoderCanaryEvidence canary, DateTimeOffset now)
    {
        if (current.State != DecoderProposalState.Canary || !SerialDeviceRecordValidator.Text(canary.ScopeId, 256) ||
            canary.SampleCount <= 0 || canary.FailureCount < 0 || canary.FailureCount > canary.SampleCount ||
            !SerialDeviceRecordValidator.IsSha256(canary.EvidenceHash))
            throw new InvalidOperationException("Decoder canary evidence is invalid.");
        return canary.Passed
            ? Next(current, DecoderProposalState.Active, now, canary: canary)
            : Next(current, DecoderProposalState.Quarantined, now, canary: canary);
    }

    public static DecoderProposalSnapshot Quarantine(DecoderProposalSnapshot current, DateTimeOffset now) =>
        current.State is DecoderProposalState.Canary or DecoderProposalState.Active
            ? Next(current, DecoderProposalState.Quarantined, now)
            : throw new InvalidOperationException("Only canary or active decoders can be quarantined.");

    public static DecoderProposalSnapshot Rollback(DecoderProposalSnapshot current, DateTimeOffset now) =>
        current.State is DecoderProposalState.Active or DecoderProposalState.Quarantined
            ? Next(current, DecoderProposalState.RolledBack, now)
            : throw new InvalidOperationException("Only an active decoder can be rolled back.");

    private static DecoderProposalSnapshot Next(
        DecoderProposalSnapshot current, DecoderProposalState state, DateTimeOffset now,
        DecoderEvaluationEvidence? evaluation = null, ActorId? approver = null, DecoderCanaryEvidence? canary = null) =>
        Create(current with
        {
            State = state,
            Evaluation = evaluation ?? current.Evaluation,
            ApproverId = approver ?? current.ApproverId,
            Canary = canary ?? current.Canary,
            UpdatedAtUtc = now,
            Version = checked(current.Version + 1),
            PreviousSnapshotHash = current.SnapshotHash,
            SnapshotHash = string.Empty,
        });

    private static DecoderProposalSnapshot Create(DecoderProposalSnapshot snapshot)
    {
        var result = snapshot with { SnapshotHash = DecoderProposalSnapshot.CalculateSnapshotHash(snapshot) };
        return result.IsConsistent() ? result : throw new InvalidOperationException("Decoder snapshot is inconsistent.");
    }
}
