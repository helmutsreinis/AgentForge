using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Tools;

namespace AgentForge.Domain.Coding;

public sealed record CodingFilePatch(string RelativePath, string ExpectedContentHash, string UnifiedDiff);

public sealed record CodingPatchSet(
    string BaselineTreeHash,
    IReadOnlyList<CodingFilePatch> Files,
    string PatchHash);

public sealed record CodingFileChangeEvidence(
    string RelativePath,
    string BeforeHash,
    string AfterHash,
    int AddedLines,
    int RemovedLines);

public sealed record CodingPatchReceipt(
    string PatchHash,
    IReadOnlyList<CodingFileChangeEvidence> Files,
    DateTimeOffset AppliedAt,
    string ReceiptHash);

public enum CodingVerificationKind
{
    Build,
    Test,
    Analyzer,
    Format,
    Coverage,
    Security,
    Dependency,
    Review,
    Publish,
}

public sealed record CodingVerificationCommand(
    CodingVerificationKind Kind,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout,
    int MaximumOutputBytes,
    ProcessSandboxKind RequiredSandbox,
    ProcessNetworkPolicy NetworkPolicy,
    bool Required);

public sealed record CodingVerificationPlan(
    IReadOnlyList<CodingVerificationCommand> Commands,
    string PlanHash);

public sealed record CodingVerificationResult(
    CodingVerificationKind Kind,
    bool Passed,
    int ExitCode,
    string StandardOutputHash,
    string StandardErrorHash,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string SandboxEvidence);

public sealed record CodingVerificationReceipt(
    string PlanHash,
    IReadOnlyList<CodingVerificationResult> Results,
    bool Passed,
    string ReceiptHash);

public sealed record CodingBackendDescriptor(
    string Id,
    string Version,
    bool IsExternal,
    IReadOnlyList<string> Languages,
    bool SupportsPlanning,
    bool SupportsPatchProposal);

public sealed record CodingBackendRequest(
    CodingSessionId SessionId,
    string BaselineCommit,
    string BaselineTreeHash,
    CodingAuthoritySnapshot Authority,
    string RepositoryProfileHash,
    string Objective,
    string PlanHash,
    IReadOnlyList<string> SelectedInstructionHashes);

public sealed record CodingBackendProposal(
    string BackendId,
    string BackendVersion,
    CodingPatchSet Patch,
    string EvidenceHash);

public static class CodingPatchValidator
{
    public const string EmptyContentHash =
        "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public static DomainResult<CodingPatchSet> Create(
        string baselineTreeHash,
        IReadOnlyList<CodingFilePatch> files)
    {
        if (!CodingRecordValidator.IsGitHash(baselineTreeHash) || files is null || files.Count is < 1 or > 128 ||
            files.Any(file => !IsPath(file.RelativePath) || !CodingRecordValidator.IsSha256(file.ExpectedContentHash) ||
                string.IsNullOrWhiteSpace(file.UnifiedDiff) || file.UnifiedDiff.Length > 1_048_576) ||
            files.Select(file => file.RelativePath).Distinct(StringComparer.Ordinal).Count() != files.Count ||
            files.Sum(file => (long)file.UnifiedDiff.Length) > 4_194_304)
        {
            return DomainResult.Fail<CodingPatchSet>(new DomainFailure(
                FailureCode.ValidationFailure, "The coding patch set is invalid or exceeds its bounds."));
        }

        var ordered = files.OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .Select(file => file with { }).ToArray();
        var hash = ComputePatchHash(baselineTreeHash, ordered);
        return DomainResult.Success(new CodingPatchSet(baselineTreeHash, ordered, hash));
    }

    public static DomainResult<CodingVerificationPlan> CreateVerificationPlan(
        IReadOnlyList<CodingVerificationCommand> commands)
    {
        if (commands is null || commands.Count is < 1 or > 32 || commands.Any(command =>
                !Enum.IsDefined(command.Kind) || !Path.IsPathFullyQualified(command.ExecutablePath) ||
                command.Arguments is null || command.Arguments.Count > 128 ||
                command.Arguments.Any(value => value.Length > 4_096 || value.Any(char.IsControl)) ||
                !IsRelativeDirectory(command.WorkingDirectory) || command.Environment is null ||
                command.Environment.Count > 32 || command.Environment.Any(item =>
                    string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 128 || item.Key.Any(character =>
                        !(char.IsAsciiLetterOrDigit(character) || character == '_')) ||
                    item.Value.Length > 4_096 || item.Value.Any(char.IsControl)) ||
                command.Timeout < TimeSpan.FromSeconds(1) || command.Timeout > TimeSpan.FromHours(1) ||
                command.MaximumOutputBytes is < 1_024 or > 4_194_304 || !Enum.IsDefined(command.RequiredSandbox) ||
                !Enum.IsDefined(command.NetworkPolicy)))
        {
            return DomainResult.Fail<CodingVerificationPlan>(new DomainFailure(
                FailureCode.ValidationFailure, "The coding verification plan is invalid or exceeds its bounds."));
        }

        var builder = new StringBuilder();
        foreach (var command in commands)
        {
            Append(builder, command.Kind); Append(builder, command.ExecutablePath);
            foreach (var value in command.Arguments) Append(builder, value);
            Append(builder, command.WorkingDirectory);
            foreach (var item in command.Environment.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                Append(builder, item.Key); Append(builder, item.Value);
            }
            Append(builder, command.Timeout.Ticks); Append(builder, command.MaximumOutputBytes);
            Append(builder, command.RequiredSandbox); Append(builder, command.NetworkPolicy); Append(builder, command.Required);
        }

        return DomainResult.Success(new CodingVerificationPlan(
            commands.Select(command => command with
            {
                Arguments = command.Arguments.ToArray(),
                Environment = new Dictionary<string, string>(command.Environment, StringComparer.Ordinal),
            }).ToArray(),
            Hash(builder.ToString())));
    }

    public static bool IsValid(CodingPatchSet? patch) => patch is not null &&
        Create(patch.BaselineTreeHash, patch.Files) is { IsSuccess: true } result &&
        string.Equals(result.Value.PatchHash, patch.PatchHash, StringComparison.Ordinal);

    public static bool IsPath(string path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 512 &&
        !Path.IsPathRooted(path) && !path.Contains('\\') &&
        path.Split('/').All(part => part is not ("" or "." or "..")) &&
        path.All(character => !char.IsControl(character));

    private static bool IsRelativeDirectory(string path) => path == "." || IsPath(path);

    private static string ComputePatchHash(string baseline, IReadOnlyList<CodingFilePatch> files)
    {
        var builder = new StringBuilder();
        Append(builder, baseline);
        foreach (var file in files)
        {
            Append(builder, file.RelativePath); Append(builder, file.ExpectedContentHash); Append(builder, file.UnifiedDiff);
        }
        return Hash(builder.ToString());
    }

    public static string Hash(string value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";

    public static void Append(StringBuilder builder, object value)
    {
        var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        builder.Append(text.Length).Append(':').Append(text).Append(';');
    }
}
