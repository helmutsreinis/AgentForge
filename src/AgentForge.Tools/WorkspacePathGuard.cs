using AgentForge.Domain.Primitives;

namespace AgentForge.Tools;

internal sealed record ContainedWorkingDirectory(string WorkspaceRoot, string WorkingDirectory);
internal sealed record ContainedWorkspaceTarget(string WorkspaceRoot, string TargetPath);

internal static class WorkspacePathGuard
{
    public static DomainResult<ContainedWorkingDirectory> Resolve(
        string workspaceRoot,
        string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(workingDirectory) ||
            workspaceRoot.Length > 2048 || workingDirectory.Length > 2048)
        {
            return Denied("Workspace and working directory must be bounded absolute paths.");
        }

        try
        {
            if (!Path.IsPathFullyQualified(workspaceRoot) || !Path.IsPathFullyQualified(workingDirectory))
            {
                return Denied("Workspace and working directory must be fully qualified.");
            }

            var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
            var working = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
            if (OperatingSystem.IsWindows() && (IsUnc(workspace) || IsUnc(working)))
            {
                return Denied("Restricted host execution does not accept UNC working paths.");
            }

            if (!Directory.Exists(workspace) || !Directory.Exists(working) || !IsContained(workspace, working))
            {
                return Denied("Working directory must exist within the configured workspace.");
            }

            if (ContainsLinkOrReparsePoint(workspace) || ContainsLinkOrReparsePoint(working))
            {
                return Denied("Restricted host working paths cannot traverse links or reparse points.");
            }

            return DomainResult.Success(new ContainedWorkingDirectory(workspace, working));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException or PathTooLongException or UnauthorizedAccessException)
        {
            return Denied("Workspace containment could not be verified.");
        }
    }

    public static DomainResult<ContainedWorkspaceTarget> ResolveTarget(
        string workspaceRoot,
        string targetPath,
        bool requireDirectory)
    {
        var workspace = Resolve(workspaceRoot, workspaceRoot);
        if (!workspace.IsSuccess)
        {
            return DomainResult.Fail<ContainedWorkspaceTarget>(workspace.Failure!);
        }

        try
        {
            if (string.IsNullOrWhiteSpace(targetPath) || targetPath.Length > 2048 ||
                !Path.IsPathFullyQualified(targetPath))
            {
                return DeniedTarget("Tool target must be a bounded absolute path.");
            }

            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
            if (!IsContained(workspace.Value.WorkspaceRoot, target) ||
                (requireDirectory ? !Directory.Exists(target) : !File.Exists(target)))
            {
                return DeniedTarget("Tool target must exist within the configured workspace.");
            }

            if (ContainsLinkOrReparsePoint(target))
            {
                return DeniedTarget("Tool target cannot traverse links or reparse points.");
            }

            return DomainResult.Success(new ContainedWorkspaceTarget(workspace.Value.WorkspaceRoot, target));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException or PathTooLongException or UnauthorizedAccessException)
        {
            return DeniedTarget("Tool target containment could not be verified.");
        }
    }

    private static bool IsContained(string workspace, string working)
    {
        var relative = Path.GetRelativePath(workspace, working);
        return relative == "." ||
            !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal static bool ContainsLinkOrReparsePoint(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return true;
        }

        var current = Path.TrimEndingDirectorySeparator(root);
        foreach (var segment in path[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var directory = new DirectoryInfo(current);
            if (directory.LinkTarget is not null ||
                (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnc(string path) =>
        path.StartsWith("\\\\", StringComparison.Ordinal) ||
        path.StartsWith("//", StringComparison.Ordinal);

    private static DomainResult<ContainedWorkingDirectory> Denied(string message) =>
        DomainResult.Fail<ContainedWorkingDirectory>(new DomainFailure(FailureCode.PolicyDenied, message));

    private static DomainResult<ContainedWorkspaceTarget> DeniedTarget(string message) =>
        DomainResult.Fail<ContainedWorkspaceTarget>(new DomainFailure(FailureCode.PolicyDenied, message));
}
