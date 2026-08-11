using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain.Artifacts;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Skills;

public enum SkillPackageStatus
{
    Installed,
    Active,
    Quarantined,
    Archived,
}

public enum SkillPackageProvenance
{
    Seed,
    User,
    AgentProposal,
}

public sealed record SkillPackageDescriptor(
    SkillId Id,
    SkillVersion Version,
    string Description,
    IReadOnlyList<SkillDependency> Dependencies,
    SkillRequirements Requirements,
    IReadOnlyList<string> Permissions,
    string ManifestHash,
    string PackageHash,
    SkillSignature? Signature);

public sealed record RegisteredSkillVersion(
    InstallationId InstallationId,
    SkillPackageDescriptor Package,
    ArtifactReference Artifact,
    SkillPackageStatus Status,
    SkillPackageProvenance Provenance,
    long RecordVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ActorId ActorId,
    CorrelationId CorrelationId);

public readonly record struct SkillProposalId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public enum SkillProposalState
{
    Proposed,
    AwaitingApproval,
    Approved,
    Canary,
    Promoted,
    Rejected,
    Quarantined,
    RolledBack,
}

public sealed record SkillEvaluationReceipt(
    bool TargetPassed,
    bool HoldoutPassed,
    bool AdversarialPassed,
    decimal BaselineScore,
    decimal CandidateScore,
    string EvidenceHash);

public sealed record SkillCanaryReceipt(
    bool Passed,
    decimal BaselineMetric,
    decimal CandidateMetric,
    string EvidenceHash);

public sealed record SkillProposal(
    SkillProposalId Id,
    InstallationId InstallationId,
    SkillId SkillId,
    SkillVersion CandidateVersion,
    string CandidatePackageHash,
    SkillVersion? BaselineVersion,
    string? BaselinePackageHash,
    IReadOnlyList<string> AddedPermissions,
    IReadOnlyList<string> RemovedPermissions,
    SkillProposalState State,
    SkillEvaluationReceipt? Evaluation,
    SkillCanaryReceipt? Canary,
    ActorId ProposedBy,
    ActorId? ApprovedBy,
    long Version,
    string PreviousSnapshotHash,
    string SnapshotHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    CorrelationId CorrelationId,
    CorrelationId? CausationId);

public readonly record struct SkillRunSnapshotId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public sealed record SkillRunSelection(
    SkillId SkillId,
    SkillVersion Version,
    string PackageHash,
    ArtifactReference Artifact,
    IReadOnlyList<string> Permissions);

public sealed record SkillRunSnapshot(
    SkillRunSnapshotId Id,
    InstallationId InstallationId,
    IReadOnlyList<SkillRunSelection> Selections,
    string SnapshotHash,
    DateTimeOffset CreatedAt,
    ActorId ActorId,
    string IdempotencyKey,
    CorrelationId CorrelationId,
    CorrelationId? CausationId);

public static class SkillGovernanceStateMachine
{
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    public const string EmptyHash =
        "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public static DomainResult<SkillProposal> Create(
        SkillProposalId id,
        RegisteredSkillVersion candidate,
        RegisteredSkillVersion? baseline,
        ActorId proposedBy,
        CorrelationId correlationId,
        CorrelationId? causationId,
        DateTimeOffset createdAt)
    {
        if (id.Value == Guid.Empty || !IsValid(candidate) || candidate.Status is not SkillPackageStatus.Installed ||
            baseline is not null && (!IsValid(baseline) || baseline.Status is not SkillPackageStatus.Active ||
                baseline.InstallationId != candidate.InstallationId || baseline.Package.Id != candidate.Package.Id) ||
            baseline is not null && baseline.Package.Version >= candidate.Package.Version ||
            !IsBounded(proposedBy.Value, 256) || !IsBounded(correlationId.Value, 128) ||
            causationId is { } causation && !IsBounded(causation.Value, 128))
        {
            return Invalid("A proposal requires an installed newer candidate and exact active baseline authority.");
        }

        var baselinePermissions = baseline?.Package.Permissions.ToHashSet(StringComparer.Ordinal) ?? [];
        var candidatePermissions = candidate.Package.Permissions.ToHashSet(StringComparer.Ordinal);
        var proposal = new SkillProposal(
            id,
            candidate.InstallationId,
            candidate.Package.Id,
            candidate.Package.Version,
            candidate.Package.PackageHash,
            baseline?.Package.Version,
            baseline?.Package.PackageHash,
            candidatePermissions.Except(baselinePermissions, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            baselinePermissions.Except(candidatePermissions, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            SkillProposalState.Proposed,
            null,
            null,
            proposedBy,
            null,
            0,
            EmptyHash,
            EmptyHash,
            createdAt,
            createdAt,
            correlationId,
            causationId);
        return DomainResult.Success(proposal with { SnapshotHash = ComputeHash(proposal) });
    }

    public static DomainResult<SkillProposal> Evaluate(
        SkillProposal current,
        SkillEvaluationReceipt receipt,
        DateTimeOffset occurredAt)
    {
        if (!CanTransition(current, SkillProposalState.Proposed, occurredAt) || !IsValid(receipt))
        {
            return Invalid("Proposal evaluation requires current proposal and bounded deterministic evidence.");
        }

        var passed = receipt.TargetPassed && receipt.HoldoutPassed && receipt.AdversarialPassed &&
            receipt.CandidateScore >= receipt.BaselineScore;
        return Next(
            current,
            passed ? SkillProposalState.AwaitingApproval : SkillProposalState.Rejected,
            occurredAt,
            evaluation: receipt);
    }

    public static DomainResult<SkillProposal> Approve(
        SkillProposal current,
        ActorId approvedBy,
        string? currentBaselineHash,
        DateTimeOffset occurredAt)
    {
        if (!CanTransition(current, SkillProposalState.AwaitingApproval, occurredAt) ||
            !IsBounded(approvedBy.Value, 256) || approvedBy == current.ProposedBy ||
            !string.Equals(current.BaselinePackageHash, currentBaselineHash, StringComparison.Ordinal))
        {
            return DomainResult.Fail<SkillProposal>(new DomainFailure(
                FailureCode.PolicyDenied,
                "Approval requires a separate actor and the exact current baseline hash."));
        }

        return Next(current, SkillProposalState.Approved, occurredAt, approvedBy: approvedBy);
    }

    public static DomainResult<SkillProposal> StartCanary(
        SkillProposal current,
        DateTimeOffset occurredAt) =>
        !CanTransition(current, SkillProposalState.Approved, occurredAt)
            ? Invalid("Only an approved proposal can start a canary.")
            : Next(current, SkillProposalState.Canary, occurredAt);

    public static DomainResult<SkillProposal> FinishCanary(
        SkillProposal current,
        SkillCanaryReceipt receipt,
        string? currentBaselineHash,
        DateTimeOffset occurredAt)
    {
        if (!CanTransition(current, SkillProposalState.Canary, occurredAt) || !IsValid(receipt) ||
            !string.Equals(current.BaselinePackageHash, currentBaselineHash, StringComparison.Ordinal))
        {
            return DomainResult.Fail<SkillProposal>(new DomainFailure(
                FailureCode.ConcurrencyConflict,
                "Canary completion requires exact fresh baseline and bounded evidence."));
        }

        var passed = receipt.Passed && receipt.CandidateMetric >= receipt.BaselineMetric;
        return Next(
            current,
            passed ? SkillProposalState.Promoted : SkillProposalState.Quarantined,
            occurredAt,
            canary: receipt);
    }

    public static DomainResult<SkillProposal> Rollback(
        SkillProposal current,
        string evidenceHash,
        DateTimeOffset occurredAt) =>
        !CanTransition(current, SkillProposalState.Promoted, occurredAt) || !IsHash(evidenceHash)
            ? Invalid("Only a promoted proposal can roll back with evidence.")
            : Next(
                current,
                SkillProposalState.RolledBack,
                occurredAt,
                canary: new SkillCanaryReceipt(false, 0, 0, evidenceHash));

    public static bool IsConsistent(SkillProposal? proposal) => proposal is not null &&
        proposal.Id.Value != Guid.Empty && proposal.InstallationId.Value != Guid.Empty &&
        IsSkillId(proposal.SkillId) && SkillVersion.TryParse(proposal.CandidateVersion.Value, out _) &&
        IsHash(proposal.CandidatePackageHash) &&
        (proposal.BaselineVersion is null || SkillVersion.TryParse(proposal.BaselineVersion.Value.Value, out _)) &&
        (proposal.BaselinePackageHash is null || IsHash(proposal.BaselinePackageHash)) &&
        IsSortedDistinct(proposal.AddedPermissions) && IsSortedDistinct(proposal.RemovedPermissions) &&
        !proposal.AddedPermissions.Intersect(proposal.RemovedPermissions, StringComparer.Ordinal).Any() &&
        Enum.IsDefined(proposal.State) && (proposal.Evaluation is null || IsValid(proposal.Evaluation)) &&
        (proposal.Canary is null || IsValid(proposal.Canary)) && IsBounded(proposal.ProposedBy.Value, 256) &&
        (proposal.ApprovedBy is null || IsBounded(proposal.ApprovedBy.Value.Value, 256)) &&
        proposal.Version >= 0 && IsHash(proposal.PreviousSnapshotHash) && IsHash(proposal.SnapshotHash) &&
        proposal.UpdatedAt >= proposal.CreatedAt && IsBounded(proposal.CorrelationId.Value, 128) &&
        (proposal.CausationId is null || IsBounded(proposal.CausationId.Value.Value, 128)) &&
        string.Equals(proposal.SnapshotHash, ComputeHash(proposal), StringComparison.Ordinal);

    public static DomainResult<SkillRunSnapshot> CreateRunSnapshot(
        SkillRunSnapshotId id,
        InstallationId installationId,
        IReadOnlyList<RegisteredSkillVersion> selected,
        ActorId actorId,
        string idempotencyKey,
        CorrelationId correlationId,
        CorrelationId? causationId,
        DateTimeOffset createdAt)
    {
        if (id.Value == Guid.Empty || installationId.Value == Guid.Empty || selected is null ||
            selected.Count is < 1 or > 128 || selected.Any(item => !IsValid(item) ||
                item.InstallationId != installationId ||
                item.Status is SkillPackageStatus.Quarantined or SkillPackageStatus.Archived) ||
            selected.Select(item => item.Package.Id).Distinct().Count() != selected.Count ||
            !IsBounded(actorId.Value, 256) || !IsBounded(idempotencyKey, 256) ||
            !IsBounded(correlationId.Value, 128) ||
            causationId is { } causation && !IsBounded(causation.Value, 128))
        {
            return DomainResult.Fail<SkillRunSnapshot>(new DomainFailure(
                FailureCode.ValidationFailure,
                "A run snapshot requires exact usable skill versions and bounded authority."));
        }

        var selections = selected.OrderBy(item => item.Package.Id.Value, StringComparer.Ordinal)
            .Select(item => new SkillRunSelection(
                item.Package.Id,
                item.Package.Version,
                item.Package.PackageHash,
                item.Artifact,
                item.Package.Permissions.Order(StringComparer.Ordinal).ToArray()))
            .ToArray();
        var snapshot = new SkillRunSnapshot(
            id,
            installationId,
            selections,
            EmptyHash,
            createdAt,
            actorId,
            idempotencyKey,
            correlationId,
            causationId);
        return DomainResult.Success(snapshot with { SnapshotHash = ComputeHash(snapshot) });
    }

    public static bool IsConsistent(SkillRunSnapshot? snapshot) => snapshot is not null &&
        snapshot.Id.Value != Guid.Empty && snapshot.InstallationId.Value != Guid.Empty &&
        snapshot.Selections.Count is >= 1 and <= 128 &&
        snapshot.Selections.SequenceEqual(
            snapshot.Selections.OrderBy(item => item.SkillId.Value, StringComparer.Ordinal)) &&
        snapshot.Selections.Select(item => item.SkillId).Distinct().Count() == snapshot.Selections.Count &&
        snapshot.Selections.All(selection => IsSkillId(selection.SkillId) &&
            SkillVersion.TryParse(selection.Version.Value, out _) && IsHash(selection.PackageHash) &&
            IsValid(selection.Artifact) && IsSortedDistinct(selection.Permissions)) &&
        IsHash(snapshot.SnapshotHash) && IsBounded(snapshot.ActorId.Value, 256) &&
        IsBounded(snapshot.IdempotencyKey, 256) && IsBounded(snapshot.CorrelationId.Value, 128) &&
        (snapshot.CausationId is null || IsBounded(snapshot.CausationId.Value.Value, 128)) &&
        string.Equals(snapshot.SnapshotHash, ComputeHash(snapshot), StringComparison.Ordinal);

    public static bool IsValid(RegisteredSkillVersion? version) => version is not null &&
        version.InstallationId.Value != Guid.Empty && IsSkillId(version.Package.Id) &&
        SkillVersion.TryParse(version.Package.Version.Value, out _) &&
        IsBounded(version.Package.Description, 2_048) && version.Package.Dependencies.Count <= 128 &&
        version.Package.Requirements is not null && IsSortedDistinctOrUnsorted(version.Package.Permissions) &&
        IsHash(version.Package.ManifestHash) && IsHash(version.Package.PackageHash) && IsValid(version.Artifact) &&
        string.Equals(version.Package.PackageHash, version.Artifact.ContentHash, StringComparison.Ordinal) &&
        Enum.IsDefined(version.Status) && Enum.IsDefined(version.Provenance) && version.RecordVersion >= 0 &&
        version.UpdatedAt >= version.CreatedAt && IsBounded(version.ActorId.Value, 256) &&
        IsBounded(version.CorrelationId.Value, 128);

    private static DomainResult<SkillProposal> Next(
        SkillProposal current,
        SkillProposalState state,
        DateTimeOffset occurredAt,
        SkillEvaluationReceipt? evaluation = null,
        SkillCanaryReceipt? canary = null,
        ActorId? approvedBy = null)
    {
        var next = current with
        {
            State = state,
            Evaluation = evaluation ?? current.Evaluation,
            Canary = canary ?? current.Canary,
            ApprovedBy = approvedBy ?? current.ApprovedBy,
            Version = current.Version + 1,
            PreviousSnapshotHash = current.SnapshotHash,
            SnapshotHash = EmptyHash,
            UpdatedAt = occurredAt,
        };
        return DomainResult.Success(next with { SnapshotHash = ComputeHash(next) });
    }

    private static bool CanTransition(
        SkillProposal? current,
        SkillProposalState state,
        DateTimeOffset occurredAt) =>
        IsConsistent(current) && current!.State == state && occurredAt >= current.UpdatedAt;

    private static bool IsValid(SkillEvaluationReceipt receipt) =>
        receipt.BaselineScore is >= 0 and <= 1_000_000 && receipt.CandidateScore is >= 0 and <= 1_000_000 &&
        IsHash(receipt.EvidenceHash);

    private static bool IsValid(SkillCanaryReceipt receipt) =>
        receipt.BaselineMetric is >= 0 and <= 1_000_000 && receipt.CandidateMetric is >= 0 and <= 1_000_000 &&
        IsHash(receipt.EvidenceHash);

    private static bool IsValid(ArtifactReference artifact) => IsHash(artifact.ContentHash) &&
        artifact.Length is >= 0 and <= 4_194_304 && IsBounded(artifact.MediaType, 256);

    private static bool IsSortedDistinct(IReadOnlyList<string> values) => values.Count <= 128 &&
        values.All(value => IsBounded(value, 256)) &&
        values.SequenceEqual(values.Order(StringComparer.Ordinal), StringComparer.Ordinal) &&
        values.Distinct(StringComparer.Ordinal).Count() == values.Count;

    private static bool IsSortedDistinctOrUnsorted(IReadOnlyList<string> values) => values.Count <= 128 &&
        values.All(value => IsBounded(value, 256)) && values.Distinct(StringComparer.Ordinal).Count() == values.Count;

    private static bool IsSkillId(SkillId id) => IsBounded(id.Value, 256) &&
        id.Value.StartsWith("skill:", StringComparison.Ordinal);

    private static bool IsHash(string? value) => value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept(LowerHex) < 0;

    private static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => !char.IsControl(character));

    private static string ComputeHash(SkillProposal proposal)
    {
        var builder = new StringBuilder(2048);
        Append(builder, proposal.Id);
        Append(builder, proposal.InstallationId);
        Append(builder, proposal.SkillId);
        Append(builder, proposal.CandidateVersion);
        Append(builder, proposal.CandidatePackageHash);
        Append(builder, proposal.BaselineVersion?.Value ?? string.Empty);
        Append(builder, proposal.BaselinePackageHash ?? string.Empty);
        foreach (var permission in proposal.AddedPermissions)
        {
            Append(builder, permission);
        }

        foreach (var permission in proposal.RemovedPermissions)
        {
            Append(builder, permission);
        }

        Append(builder, proposal.State);
        Append(builder, proposal.Evaluation?.TargetPassed ?? false);
        Append(builder, proposal.Evaluation?.HoldoutPassed ?? false);
        Append(builder, proposal.Evaluation?.AdversarialPassed ?? false);
        Append(builder, proposal.Evaluation?.BaselineScore ?? 0);
        Append(builder, proposal.Evaluation?.CandidateScore ?? 0);
        Append(builder, proposal.Evaluation?.EvidenceHash ?? string.Empty);
        Append(builder, proposal.Canary?.Passed ?? false);
        Append(builder, proposal.Canary?.BaselineMetric ?? 0);
        Append(builder, proposal.Canary?.CandidateMetric ?? 0);
        Append(builder, proposal.Canary?.EvidenceHash ?? string.Empty);
        Append(builder, proposal.ProposedBy);
        Append(builder, proposal.ApprovedBy?.Value ?? string.Empty);
        Append(builder, proposal.Version);
        Append(builder, proposal.PreviousSnapshotHash);
        Append(builder, proposal.CreatedAt.UtcTicks);
        Append(builder, proposal.UpdatedAt.UtcTicks);
        Append(builder, proposal.CorrelationId);
        Append(builder, proposal.CausationId?.Value ?? string.Empty);
        return Hash(builder.ToString());
    }

    private static string ComputeHash(SkillRunSnapshot snapshot)
    {
        var builder = new StringBuilder(2048);
        Append(builder, snapshot.Id);
        Append(builder, snapshot.InstallationId);
        foreach (var selection in snapshot.Selections)
        {
            Append(builder, selection.SkillId);
            Append(builder, selection.Version);
            Append(builder, selection.PackageHash);
            Append(builder, selection.Artifact.ContentHash);
            Append(builder, selection.Artifact.Length);
            Append(builder, selection.Artifact.MediaType);
            Append(builder, selection.Artifact.CreatedAt.UtcTicks);
            foreach (var permission in selection.Permissions)
            {
                Append(builder, permission);
            }
        }

        Append(builder, snapshot.CreatedAt.UtcTicks);
        Append(builder, snapshot.ActorId);
        Append(builder, snapshot.IdempotencyKey);
        Append(builder, snapshot.CorrelationId);
        Append(builder, snapshot.CausationId?.Value ?? string.Empty);
        return Hash(builder.ToString());
    }

    private static string Hash(string value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";

    private static void Append(StringBuilder builder, object value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        builder.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append(';');
    }

    private static DomainResult<SkillProposal> Invalid(string message) =>
        DomainResult.Fail<SkillProposal>(new DomainFailure(FailureCode.ValidationFailure, message));
}
