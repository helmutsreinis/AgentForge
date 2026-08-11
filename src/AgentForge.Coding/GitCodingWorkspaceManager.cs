using System.Diagnostics;
using AgentForge.Abstractions.Coding;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Coding;
using AgentForge.Domain.Primitives;

namespace AgentForge.Coding;

internal sealed class GitCodingWorkspaceManager(IClock clock) : ICodingWorkspaceManager
{
    public async Task<DomainResult<CodingWorkspace>> CreateAsync(
        CodingWorkspaceRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || request.SessionId.Value == Guid.Empty || !CodingRecordValidator.IsGitHash(request.BaselineCommit) ||
            !CodingRecordValidator.IsBranch(request.BranchName) || !TryDirectory(request.RepositoryRoot, out var repository) ||
            !TryDirectory(request.WorkspaceParent, out var parent))
        {
            return Invalid<CodingWorkspace>("The coding workspace request is invalid.");
        }

        var top = await GitAsync(repository!, ["rev-parse", "--show-toplevel"], cancellationToken);
        if (!top.IsSuccess || !PathEquals(repository!, top.Value.Trim()))
        {
            return Invalid<CodingWorkspace>("The source path is not the exact Git repository root.");
        }

        var status = await GitAsync(repository!, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken);
        if (!status.IsSuccess)
        {
            return DomainResult.Fail<CodingWorkspace>(status.Failure!);
        }

        var sourceWasClean = status.Value.Length == 0;
        if (request.RequireCleanSource && !sourceWasClean)
        {
            return DomainResult.Fail<CodingWorkspace>(new DomainFailure(
                FailureCode.PolicyDenied, "The source worktree contains operator changes."));
        }

        var commit = await GitAsync(repository!, ["rev-parse", "--verify", $"{request.BaselineCommit}^{{commit}}"], cancellationToken);
        if (!commit.IsSuccess || !string.Equals(commit.Value.Trim(), request.BaselineCommit, StringComparison.Ordinal))
        {
            return Invalid<CodingWorkspace>("The baseline commit is not an exact local commit.");
        }

        var tree = await GitAsync(repository!, ["rev-parse", $"{request.BaselineCommit}^{{tree}}"], cancellationToken);
        if (!tree.IsSuccess || !CodingRecordValidator.IsGitHash(tree.Value.Trim()))
        {
            return Invalid<CodingWorkspace>("The baseline tree could not be resolved.");
        }

        var target = Path.GetFullPath(Path.Combine(parent!, $"agentforge-{request.SessionId.Value:N}"));
        if (!IsWithin(parent!, target) || Directory.Exists(target) || File.Exists(target))
        {
            return Invalid<CodingWorkspace>("The isolated worktree target already exists or escaped its parent.");
        }

        var added = await GitAsync(repository!, ["worktree", "add", "--detach", target, request.BaselineCommit], cancellationToken);
        if (!added.IsSuccess)
        {
            return DomainResult.Fail<CodingWorkspace>(added.Failure!);
        }

        var switched = await GitAsync(target, ["switch", "-c", request.BranchName], cancellationToken);
        if (!switched.IsSuccess)
        {
            _ = await GitAsync(repository!, ["worktree", "remove", "--force", target], CancellationToken.None);
            return DomainResult.Fail<CodingWorkspace>(switched.Failure!);
        }

        var workspace = new CodingWorkspace(
            request.SessionId,
            repository!,
            target,
            request.BaselineCommit,
            tree.Value.Trim(),
            request.BranchName,
            sourceWasClean,
            clock.UtcNow);
        return CodingRecordValidator.IsValid(workspace)
            ? DomainResult.Success(workspace)
            : Invalid<CodingWorkspace>("The created worktree evidence is inconsistent.");
    }

    public async Task<DomainResult<bool>> RemoveAsync(
        CodingWorkspace workspace,
        CancellationToken cancellationToken)
    {
        if (!CodingRecordValidator.IsValid(workspace) || !Directory.Exists(workspace.WorktreeRoot) ||
            PathEquals(workspace.RepositoryRoot, workspace.WorktreeRoot))
        {
            return Invalid<bool>("The exact managed coding worktree is unavailable.");
        }

        var marker = Path.Combine(workspace.WorktreeRoot, ".git");
        if (!File.Exists(marker) || new FileInfo(marker).Length > 4_096)
        {
            return Invalid<bool>("The managed Git worktree marker is invalid.");
        }

        var markerText = await File.ReadAllTextAsync(marker, cancellationToken);
        if (!markerText.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase) ||
            !markerText.Contains("worktrees", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid<bool>("The target is not a managed linked worktree.");
        }

        var status = await GitAsync(workspace.WorktreeRoot, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken);
        if (!status.IsSuccess || status.Value.Length != 0)
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.PolicyDenied, "A coding worktree with uncommitted changes cannot be removed."));
        }

        var removed = await GitAsync(
            workspace.RepositoryRoot,
            ["worktree", "remove", workspace.WorktreeRoot],
            cancellationToken);
        return removed.IsSuccess ? DomainResult.Success(true) : DomainResult.Fail<bool>(removed.Failure!);
    }

    private static async Task<DomainResult<string>> GitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                return External("Git could not be started.");
            }

            var outputTask = ReadBoundedAsync(process.StandardOutput, cancellationToken);
            var errorTask = ReadBoundedAsync(process.StandardError, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            return process.ExitCode == 0
                ? DomainResult.Success(output)
                : External(string.IsNullOrWhiteSpace(error) ? "Git rejected the bounded operation." : error.Trim());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return DomainResult.Fail<string>(new DomainFailure(FailureCode.BudgetExceeded, "Git exceeded its time bound."));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            TryKill(process);
            return External("Git is unavailable for the requested workspace operation.");
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var builder = new System.Text.StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (builder.Length + read > 65_536)
            {
                throw new IOException("Git output exceeded its bound.");
            }

            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    private static bool TryDirectory(string value, out string? fullPath)
    {
        fullPath = null;
        try
        {
            fullPath = Path.GetFullPath(value);
            return Directory.Exists(fullPath) && Path.IsPathFullyQualified(fullPath) &&
                (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsWithin(string root, string path) => path.StartsWith(
        root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool PathEquals(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static DomainResult<T> Invalid<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<string> External(string message) =>
        DomainResult.Fail<string>(new DomainFailure(FailureCode.RecoverableExternalFailure, message));
}
