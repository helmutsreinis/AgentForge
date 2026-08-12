using System.Text;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Skills;

namespace AgentForge.Domain.Learning;

public readonly record struct SkillBundleId(string Value)
{
    public override string ToString() => Value;
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
