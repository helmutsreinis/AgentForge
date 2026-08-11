using System.Security.Cryptography;
using System.Text;
using AgentForge.Abstractions.Coding;
using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Coding;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Tools;

namespace AgentForge.Coding;

internal sealed class SandboxCodingVerifier(ISandbox sandbox) : ICodingVerifier
{
    private const ProcessIsolationFeature RequiredFeatures =
        ProcessIsolationFeature.DirectExecutable |
        ProcessIsolationFeature.ArgumentArray |
        ProcessIsolationFeature.EnvironmentAllowlist |
        ProcessIsolationFeature.WorkingDirectoryContainment |
        ProcessIsolationFeature.BoundedOutput |
        ProcessIsolationFeature.WallClockTimeout |
        ProcessIsolationFeature.ProcessTreeTermination;

    public async Task<DomainResult<CodingVerificationReceipt>> VerifyAsync(
        CodingWorkspace workspace,
        CodingAuthoritySnapshot authority,
        CodingVerificationPlan plan,
        CancellationToken cancellationToken)
    {
        var recreated = CodingPatchValidator.CreateVerificationPlan(plan.Commands);
        if (!CodingRecordValidator.IsValid(workspace) || !CodingRecordValidator.IsValid(authority) ||
            authority.WorkspaceHash != CodingRecordValidator.ComputeWorkspaceHash(workspace) ||
            !recreated.IsSuccess || recreated.Value.PlanHash != plan.PlanHash)
        {
            return Invalid("Verification requires exact workspace, authority, and plan evidence.");
        }

        if (plan.Commands.Any(command => command.Kind is not CodingVerificationKind.Review &&
                (command.RequiredSandbox is not ProcessSandboxKind.Container ||
                 command.NetworkPolicy is not ProcessNetworkPolicy.Denied)))
        {
            return DomainResult.Fail<CodingVerificationReceipt>(new DomainFailure(
                FailureCode.UnsupportedCapability,
                "Project build, test, analyzer, format, coverage, security, dependency, and publish commands require denied-network container isolation."));
        }

        if (plan.Commands.Any(command => command.Kind is CodingVerificationKind.Publish) &&
            authority.ExternalMutationApprovalHash is null)
        {
            return DomainResult.Fail<CodingVerificationReceipt>(new DomainFailure(
                FailureCode.ApprovalRequired, "Publishing requires an exact external-mutation approval."));
        }

        var results = new List<CodingVerificationResult>();
        foreach (var command in plan.Commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryWorkingDirectory(workspace.WorktreeRoot, command.WorkingDirectory, out var workingDirectory))
            {
                return Invalid("A verification working directory escaped the coding worktree.");
            }

            var execution = await sandbox.ExecuteAsync(new ProcessExecutionRequest(
                command.ExecutablePath,
                command.Arguments,
                workspace.WorktreeRoot,
                workingDirectory!,
                command.Environment,
                command.Timeout,
                command.MaximumOutputBytes,
                command.NetworkPolicy,
                command.RequiredSandbox,
                RequiredFeatures | (command.Kind is CodingVerificationKind.Review
                    ? ProcessIsolationFeature.None
                    : ProcessIsolationFeature.NetworkIsolation | ProcessIsolationFeature.FileSystemIsolation)),
                null,
                cancellationToken);
            if (!execution.IsSuccess)
            {
                return DomainResult.Fail<CodingVerificationReceipt>(execution.Failure!);
            }

            var result = new CodingVerificationResult(
                command.Kind,
                execution.Value.ExitCode == 0,
                execution.Value.ExitCode,
                Hash(execution.Value.StandardOutput),
                Hash(execution.Value.StandardError),
                execution.Value.StartedAt,
                execution.Value.CompletedAt,
                Bound(execution.Value.Sandbox.Evidence, 2_048));
            results.Add(result);
            if (!result.Passed && command.Required) break;
        }

        var passed = results.Count == plan.Commands.Count && results.Zip(plan.Commands)
            .All(item => item.First.Passed || !item.Second.Required);
        var builder = new StringBuilder();
        CodingPatchValidator.Append(builder, plan.PlanHash);
        foreach (var result in results)
        {
            CodingPatchValidator.Append(builder, result.Kind); CodingPatchValidator.Append(builder, result.Passed);
            CodingPatchValidator.Append(builder, result.ExitCode); CodingPatchValidator.Append(builder, result.StandardOutputHash);
            CodingPatchValidator.Append(builder, result.StandardErrorHash); CodingPatchValidator.Append(builder, result.StartedAt.UtcTicks);
            CodingPatchValidator.Append(builder, result.CompletedAt.UtcTicks); CodingPatchValidator.Append(builder, result.SandboxEvidence);
        }
        CodingPatchValidator.Append(builder, passed);
        return DomainResult.Success(new CodingVerificationReceipt(
            plan.PlanHash, results, passed, CodingPatchValidator.Hash(builder.ToString())));
    }

    private static bool TryWorkingDirectory(string root, string relative, out string? fullPath)
    {
        fullPath = null;
        if (relative != "." && !CodingPatchValidator.IsPath(relative)) return false;
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            fullPath = relative == "." ? fullRoot : Path.GetFullPath(Path.Combine(
                fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return Directory.Exists(fullPath) && (string.Equals(fullPath, fullRoot, comparison) ||
                fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)) &&
                (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Hash(ReadOnlySpan<byte> value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(value))}";

    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private static DomainResult<CodingVerificationReceipt> Invalid(string message) =>
        DomainResult.Fail<CodingVerificationReceipt>(new DomainFailure(FailureCode.ValidationFailure, message));
}
