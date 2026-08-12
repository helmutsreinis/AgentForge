using System.Text;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Skills;

namespace AgentForge.Domain.Learning;

public readonly record struct SkillBundleId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct SkillBundleProposalId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public enum SkillBundleProposalState
{
    Proposed,
    Verified,
    Critiqued,
    Active,
    Rejected,
    Archived,
}

public sealed record SkillBundleNode(
    string NodeId,
    SkillId SkillId,
    SkillVersion Version,
    string PackageHash,
    string InputContractHash,
    string OutputContractHash);

public sealed record SkillBundleEdge(string FromNodeId, string ToNodeId);

public sealed record SkillBundleDefinition(
    SkillBundleId Id,
    SkillVersion Version,
    IReadOnlyList<SkillBundleNode> Nodes,
    IReadOnlyList<SkillBundleEdge> Edges,
    IReadOnlyList<string> Permissions,
    string SourceSignalHash,
    decimal BaselineScore,
    decimal CandidateScore,
    string EvaluationEvidenceHash,
    string DefinitionHash);

public sealed record SkillBundleProposal(
    SkillBundleProposalId Id,
    InstallationId InstallationId,
    SkillBundleDefinition Definition,
    LearningRoleAssignments Roles,
    SkillBundleProposalState State,
    LearningCritique? Critique,
    long Version,
    string PreviousSnapshotHash,
    string SnapshotHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    CorrelationId CorrelationId,
    CorrelationId? CausationId);

public static class SkillBundleProposalStateMachine
{
    public static DomainResult<SkillBundleProposal> Create(
        SkillBundleProposalId id,
        InstallationId installationId,
        SkillBundleDefinition definition,
        LearningRoleAssignments roles,
        ActorId proposer,
        DateTimeOffset createdAt,
        CorrelationId correlationId,
        CorrelationId? causationId)
    {
        if (id.Value == Guid.Empty || installationId.Value == Guid.Empty ||
            !SkillBundleSynthesizer.IsConsistent(definition) || !roles.IsSeparated() ||
            roles.Proposer != proposer || !LearningValidation.IsBounded(correlationId.Value, 128))
            return Failure("A bundle proposal requires a consistent definition and assigned separated proposer.");
        var proposal = new SkillBundleProposal(
            id, installationId, definition, roles, SkillBundleProposalState.Proposed, null, 0,
            LearningValidation.EmptyHash, LearningValidation.EmptyHash, createdAt, createdAt,
            correlationId, causationId);
        return DomainResult.Success(proposal with { SnapshotHash = ComputeHash(proposal) });
    }

    public static DomainResult<SkillBundleProposal> Verify(
        SkillBundleProposal current, ActorId verifier, string evidenceHash, DateTimeOffset occurredAt)
    {
        if (!CanTransition(current, SkillBundleProposalState.Proposed, verifier, current.Roles.Verifier, occurredAt) ||
            evidenceHash != current.Definition.EvaluationEvidenceHash)
            return Failure("Bundle verification requires the assigned verifier and exact deterministic evidence.");
        return Next(current, SkillBundleProposalState.Verified, occurredAt);
    }

    public static DomainResult<SkillBundleProposal> Critique(
        SkillBundleProposal current, ActorId critic, LearningCritique critique, DateTimeOffset occurredAt)
    {
        if (!CanTransition(current, SkillBundleProposalState.Verified, critic, current.Roles.Critic, occurredAt) ||
            !IsValid(critique))
            return Failure("Bundle critique requires the assigned critic and bounded evidence.");
        return Next(current, critique.Passed ? SkillBundleProposalState.Critiqued : SkillBundleProposalState.Rejected,
            occurredAt, critique);
    }

    public static DomainResult<SkillBundleProposal> Approve(
        SkillBundleProposal current, ActorId governor, DateTimeOffset occurredAt) =>
        !CanTransition(current, SkillBundleProposalState.Critiqued, governor, current.Roles.Governor, occurredAt)
            ? Failure("Only the assigned governor can activate a critiqued bundle.")
            : Next(current, SkillBundleProposalState.Active, occurredAt);

    public static DomainResult<SkillBundleProposal> Archive(
        SkillBundleProposal current, ActorId governor, DateTimeOffset occurredAt) =>
        !CanTransition(current, SkillBundleProposalState.Active, governor, current.Roles.Governor, occurredAt)
            ? Failure("Only the assigned governor can archive an active bundle.")
            : Next(current, SkillBundleProposalState.Archived, occurredAt);

    public static bool IsConsistent(SkillBundleProposal? value) => value is not null &&
        value.Id.Value != Guid.Empty && value.InstallationId.Value != Guid.Empty &&
        SkillBundleSynthesizer.IsConsistent(value.Definition) && value.Roles.IsSeparated() &&
        Enum.IsDefined(value.State) && (value.Critique is null || IsValid(value.Critique)) && value.Version >= 0 &&
        LearningValidation.IsHash(value.PreviousSnapshotHash) && LearningValidation.IsHash(value.SnapshotHash) &&
        value.UpdatedAt >= value.CreatedAt && LearningValidation.IsBounded(value.CorrelationId.Value, 128) &&
        string.Equals(value.SnapshotHash, ComputeHash(value), StringComparison.Ordinal);

    private static bool CanTransition(
        SkillBundleProposal current, SkillBundleProposalState state, ActorId actor, ActorId expectedActor,
        DateTimeOffset occurredAt) => IsConsistent(current) && current.State == state &&
        actor == expectedActor && occurredAt >= current.UpdatedAt;

    private static DomainResult<SkillBundleProposal> Next(
        SkillBundleProposal current, SkillBundleProposalState state, DateTimeOffset occurredAt,
        LearningCritique? critique = null)
    {
        var next = current with
        {
            State = state,
            Critique = critique ?? current.Critique,
            Version = current.Version + 1,
            PreviousSnapshotHash = current.SnapshotHash,
            SnapshotHash = LearningValidation.EmptyHash,
            UpdatedAt = occurredAt,
        };
        return DomainResult.Success(next with { SnapshotHash = ComputeHash(next) });
    }

    private static string ComputeHash(SkillBundleProposal value)
    {
        var builder = new StringBuilder(2048);
        foreach (var item in new object?[]
        {
            value.Id, value.InstallationId, value.Definition.DefinitionHash, value.Roles.Worker,
            value.Roles.Proposer, value.Roles.Verifier, value.Roles.Critic, value.Roles.Governor,
            value.State, value.Critique?.Passed ?? false, value.Critique?.EvidenceHash ?? string.Empty,
        }) LearningValidation.Append(builder, item ?? string.Empty);
        foreach (var finding in value.Critique?.FindingCodes ?? []) LearningValidation.Append(builder, finding);
        foreach (var item in new object?[]
        {
            value.Version, value.PreviousSnapshotHash, value.CreatedAt.UtcTicks, value.UpdatedAt.UtcTicks,
            value.CorrelationId, value.CausationId?.Value ?? string.Empty,
        }) LearningValidation.Append(builder, item ?? string.Empty);
        return LearningValidation.Hash(builder.ToString());
    }

    private static bool IsValid(LearningCritique critique) => critique.FindingCodes.Count <= 128 &&
        critique.FindingCodes.All(code => LearningValidation.IsBounded(code, 128)) &&
        critique.FindingCodes.Distinct(StringComparer.Ordinal).Count() == critique.FindingCodes.Count &&
        LearningValidation.IsHash(critique.EvidenceHash);

    private static DomainResult<SkillBundleProposal> Failure(string message) =>
        DomainResult.Fail<SkillBundleProposal>(new DomainFailure(FailureCode.ValidationFailure, message));
}

public static class SkillBundleSynthesizer
{
    public static DomainResult<SkillBundleDefinition> Synthesize(
        SkillBundleId id,
        SkillVersion version,
        LearningSignal signal,
        LearningClassification classification,
        IReadOnlyDictionary<SkillId, IReadOnlyList<string>> exactPermissions,
        decimal baselineScore,
        decimal candidateScore,
        bool targetPassed,
        bool holdoutPassed,
        string evaluationEvidenceHash)
    {
        exactPermissions ??= new Dictionary<SkillId, IReadOnlyList<string>>();
        if (!LearningSignalClassifier.IsConsistent(signal) || classification.SignalId != signal.Id ||
            classification.Action is not LearningAction.Bundle || classification.SignalHash != signal.SignalHash ||
            !LearningValidation.IsBounded(id.Value, 256) || !id.Value.StartsWith("bundle:", StringComparison.Ordinal) ||
            !SkillVersion.TryParse(version.Value, out _) || signal.SuccessfulChain.Count < 2 ||
            baselineScore is < 0 or > 1_000_000 || candidateScore is < 0 or > 1_000_000 ||
            !targetPassed || !holdoutPassed || candidateScore < baselineScore ||
            !LearningValidation.IsHash(evaluationEvidenceHash) ||
            signal.SuccessfulChain.Any(step => !exactPermissions.ContainsKey(step.SkillId)))
        {
            return DomainResult.Fail<SkillBundleDefinition>(new DomainFailure(
                FailureCode.ValidationFailure,
                "A bundle requires a repeated successful chain, exact pinned skills, and passing baseline evidence."));
        }

        var nodes = signal.SuccessfulChain.Select(step => new SkillBundleNode(
            $"step-{step.Position + 1}", step.SkillId, step.Version, step.PackageHash,
            step.InputContractHash, step.OutputContractHash)).ToArray();
        if (nodes.Zip(nodes.Skip(1)).Any(pair => pair.First.OutputContractHash != pair.Second.InputContractHash))
        {
            return DomainResult.Fail<SkillBundleDefinition>(new DomainFailure(
                FailureCode.ValidationFailure, "Adjacent bundle contracts are incompatible."));
        }

        var edges = nodes.Zip(nodes.Skip(1), (from, to) => new SkillBundleEdge(from.NodeId, to.NodeId)).ToArray();
        var permissions = nodes.SelectMany(node => exactPermissions[node.SkillId])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (permissions.Length > 128 || permissions.Any(permission => !LearningValidation.IsBounded(permission, 256)))
        {
            return DomainResult.Fail<SkillBundleDefinition>(new DomainFailure(
                FailureCode.PolicyDenied, "The bundle permission union is invalid or exceeds its bound."));
        }

        var definition = new SkillBundleDefinition(
            id, version, nodes, edges, permissions, signal.SignalHash, baselineScore, candidateScore,
            evaluationEvidenceHash, LearningValidation.EmptyHash);
        return DomainResult.Success(definition with { DefinitionHash = ComputeHash(definition) });
    }

    public static bool IsConsistent(SkillBundleDefinition? value) => value is not null &&
        LearningValidation.IsBounded(value.Id.Value, 256) && value.Id.Value.StartsWith("bundle:", StringComparison.Ordinal) &&
        SkillVersion.TryParse(value.Version.Value, out _) && value.Nodes.Count is >= 2 and <= 128 &&
        value.Nodes.Select(node => node.NodeId).Distinct(StringComparer.Ordinal).Count() == value.Nodes.Count &&
        value.Nodes.All(node => LearningValidation.IsBounded(node.NodeId, 128) &&
            LearningValidation.IsSkillId(node.SkillId) && SkillVersion.TryParse(node.Version.Value, out _) &&
            LearningValidation.IsHash(node.PackageHash) && LearningValidation.IsHash(node.InputContractHash) &&
            LearningValidation.IsHash(node.OutputContractHash)) &&
        value.Edges.Count == value.Nodes.Count - 1 && value.Edges.Select((edge, index) => edge ==
            new SkillBundleEdge(value.Nodes[index].NodeId, value.Nodes[index + 1].NodeId)).All(equal => equal) &&
        value.Permissions.SequenceEqual(value.Permissions.Order(StringComparer.Ordinal)) &&
        value.Permissions.Distinct(StringComparer.Ordinal).Count() == value.Permissions.Count &&
        LearningValidation.IsHash(value.SourceSignalHash) && value.BaselineScore >= 0 &&
        value.CandidateScore >= value.BaselineScore && LearningValidation.IsHash(value.EvaluationEvidenceHash) &&
        LearningValidation.IsHash(value.DefinitionHash) &&
        string.Equals(value.DefinitionHash, ComputeHash(value), StringComparison.Ordinal);

    private static string ComputeHash(SkillBundleDefinition value)
    {
        var builder = new StringBuilder(4096);
        LearningValidation.Append(builder, value.Id);
        LearningValidation.Append(builder, value.Version);
        foreach (var node in value.Nodes)
        {
            LearningValidation.Append(builder, node.NodeId);
            LearningValidation.Append(builder, node.SkillId);
            LearningValidation.Append(builder, node.Version);
            LearningValidation.Append(builder, node.PackageHash);
            LearningValidation.Append(builder, node.InputContractHash);
            LearningValidation.Append(builder, node.OutputContractHash);
        }
        foreach (var edge in value.Edges)
        {
            LearningValidation.Append(builder, edge.FromNodeId);
            LearningValidation.Append(builder, edge.ToNodeId);
        }
        foreach (var permission in value.Permissions) LearningValidation.Append(builder, permission);
        LearningValidation.Append(builder, value.SourceSignalHash);
        LearningValidation.Append(builder, value.BaselineScore);
        LearningValidation.Append(builder, value.CandidateScore);
        LearningValidation.Append(builder, value.EvaluationEvidenceHash);
        return LearningValidation.Hash(builder.ToString());
    }
}
