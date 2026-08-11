using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Coding;

public readonly record struct CodingSessionId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public sealed record RepositoryInstruction(string RelativePath, string ContentHash, long Length);

public sealed record RepositoryProject(
    string RelativePath,
    string Language,
    string? Sdk,
    IReadOnlyList<string> TargetFrameworks,
    IReadOnlyList<string> ProjectReferences,
    bool IsTestProject);

public sealed record RepositoryProfile(
    string RootPath,
    string ProfileHash,
    IReadOnlyList<string> SolutionFiles,
    IReadOnlyList<RepositoryProject> Projects,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> BuildSystems,
    IReadOnlyList<string> ContinuousIntegrationFiles,
    IReadOnlyList<string> LockFiles,
    IReadOnlyList<RepositoryInstruction> Instructions,
    DateTimeOffset ObservedAt);

public sealed record SemanticLocation(
    string RelativePath,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

public sealed record SemanticSymbol(
    string Name,
    string Kind,
    string DisplayName,
    SemanticLocation Definition,
    IReadOnlyList<SemanticLocation> References);

public sealed record SemanticDiagnostic(
    string Id,
    string Severity,
    string Message,
    SemanticLocation? Location);

public sealed record SemanticQuery(string RelativePath, int Line, int Column, int MaximumReferences = 128);

public sealed record SemanticResult(
    SemanticSymbol? Symbol,
    IReadOnlyList<SemanticDiagnostic> Diagnostics,
    string EvidenceHash);

public sealed record CodingWorkspaceRequest(
    CodingSessionId SessionId,
    string RepositoryRoot,
    string WorkspaceParent,
    string BaselineCommit,
    string BranchName,
    bool RequireCleanSource = true);

public sealed record CodingWorkspace(
    CodingSessionId SessionId,
    string RepositoryRoot,
    string WorktreeRoot,
    string BaselineCommit,
    string BaselineTreeHash,
    string BranchName,
    bool SourceWasClean,
    DateTimeOffset CreatedAt);

public sealed record CodingAuthoritySnapshot(
    InstallationId InstallationId,
    AgentIdentityId AgentId,
    long AgentVersion,
    string PolicyHash,
    string CapabilityHash,
    string BudgetHash,
    string SkillSnapshotHash,
    string WorkspaceHash,
    ActorId ActorId,
    CorrelationId CorrelationId,
    CorrelationId? CausationId);

public static class CodingRecordValidator
{
    public static bool IsValid(CodingWorkspace? workspace) => workspace is not null &&
        workspace.SessionId.Value != Guid.Empty && Path.IsPathFullyQualified(workspace.RepositoryRoot) &&
        Path.IsPathFullyQualified(workspace.WorktreeRoot) && IsGitHash(workspace.BaselineCommit) &&
        IsGitHash(workspace.BaselineTreeHash) && IsBranch(workspace.BranchName) &&
        workspace.CreatedAt != default;

    public static bool IsValid(CodingAuthoritySnapshot? authority) => authority is not null &&
        authority.InstallationId.Value != Guid.Empty && authority.AgentId.Value != Guid.Empty &&
        authority.AgentVersion >= 0 && IsSha256(authority.PolicyHash) && IsSha256(authority.CapabilityHash) &&
        IsSha256(authority.BudgetHash) && IsSha256(authority.SkillSnapshotHash) &&
        IsSha256(authority.WorkspaceHash) && IsBounded(authority.ActorId.Value, 256) &&
        IsBounded(authority.CorrelationId.Value, 128) &&
        (authority.CausationId is null || IsBounded(authority.CausationId.Value.Value, 128));

    public static bool IsGitHash(string? value) => value is { Length: 40 or 64 } &&
        value.All(character => char.IsAsciiHexDigit(character) && !char.IsAsciiLetterUpper(character));

    public static bool IsSha256(string? value) => value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).ToArray().All(character => char.IsAsciiHexDigit(character) && !char.IsAsciiLetterUpper(character));

    public static bool IsBranch(string? value) => IsBounded(value, 256) &&
        !value!.StartsWith('-') && !value.EndsWith('.') && !value.Contains("..", StringComparison.Ordinal) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '-' or '_' or '.');

    private static bool IsBounded(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && value.All(character => !char.IsControl(character));
}
