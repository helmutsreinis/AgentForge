using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Time;
using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Tools;

namespace AgentForge.Tools;

internal sealed class BuiltInToolExecutor(
    IEnumerable<IBuiltInToolHandler> handlers) : IBuiltInToolExecutor
{
    public Task<DomainResult<ProcessExecutionResult>> ExecuteAsync(
        BuiltInToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var handler = handlers.SingleOrDefault(item => item.CanHandle(request.HandlerId));
        return handler is null
            ? Task.FromResult(DomainResult.Fail<ProcessExecutionResult>(new DomainFailure(
                FailureCode.UnsupportedCapability,
                "The built-in tool handler is not available.")))
            : handler.ExecuteAsync(request, cancellationToken);
    }
}

internal sealed class BuiltInWorkspaceToolHandler(IClock clock) : IBuiltInToolHandler
{
    public bool CanHandle(string handlerId) =>
        handlerId is "workspace.list" or "workspace.read-text";

    public Task<DomainResult<ProcessExecutionResult>> ExecuteAsync(
        BuiltInToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var started = clock.UtcNow;
        DomainResult<byte[]> output = request.HandlerId switch
        {
            "workspace.list" => ListDirectory(request, cancellationToken),
            "workspace.read-text" => ReadText(request, cancellationToken),
            _ => DomainResult.Fail<byte[]>(new DomainFailure(
                FailureCode.UnsupportedCapability,
                "The built-in tool handler is not available.")),
        };
        if (!output.IsSuccess)
        {
            return Task.FromResult(DomainResult.Fail<ProcessExecutionResult>(output.Failure!));
        }

        var completed = clock.UtcNow < started ? started : clock.UtcNow;
        return Task.FromResult(DomainResult.Success(new ProcessExecutionResult(
            0,
            output.Value,
            [],
            started,
            completed,
            completed - started,
            new ProcessSandboxCapabilities(
                ProcessSandboxKind.BuiltIn,
                true,
                ProcessIsolationFeature.WorkingDirectoryContainment |
                    ProcessIsolationFeature.BoundedOutput |
                    ProcessIsolationFeature.NetworkIsolation |
                    ProcessIsolationFeature.FileSystemIsolation,
                "Managed built-in handler; no child process, environment, or network access."))));
    }

    private static DomainResult<byte[]> ListDirectory(
        BuiltInToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryWholeNumber(request.Parameters, "maximumEntries", 1, 500, out var maximumEntries) ||
            request.Target is null || request.Parameters.TryGetValue("directory", out var parameter) is false ||
            !string.Equals(parameter.Text, request.Target, StringComparison.Ordinal))
        {
            return Invalid("Directory listing parameters are invalid.");
        }

        var target = WorkspacePathGuard.ResolveTarget(request.Workspace, request.Target, requireDirectory: true);
        if (!target.IsSuccess)
        {
            return DomainResult.Fail<byte[]>(target.Failure!);
        }

        try
        {
            var entries = new List<object>();
            var truncated = false;
            foreach (var path in Directory.EnumerateFileSystemEntries(target.Value.TargetPath)
                .Order(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entries.Count >= maximumEntries)
                {
                    truncated = true;
                    break;
                }

                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                entries.Add(new
                {
                    name = Path.GetFileName(path),
                    kind = isDirectory ? "directory" : "file",
                    size = isDirectory ? (long?)null : new FileInfo(path).Length,
                });
            }

            return Bounded(JsonSerializer.SerializeToUtf8Bytes(new
            {
                directory = target.Value.TargetPath,
                entries,
                truncated,
            }), request.MaximumOutputBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return External("The directory could not be read safely.");
        }
    }

    private static DomainResult<byte[]> ReadText(
        BuiltInToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryWholeNumber(request.Parameters, "maximumBytes", 1, 65_536, out var maximumBytes) ||
            request.Target is null || request.Parameters.TryGetValue("path", out var parameter) is false ||
            !string.Equals(parameter.Text, request.Target, StringComparison.Ordinal))
        {
            return Invalid("Text-read parameters are invalid.");
        }

        var target = WorkspacePathGuard.ResolveTarget(request.Workspace, request.Target, requireDirectory: false);
        if (!target.IsSuccess)
        {
            return DomainResult.Fail<byte[]>(target.Failure!);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new FileInfo(target.Value.TargetPath);
            if (file.Length > maximumBytes)
            {
                return DomainResult.Fail<byte[]>(new DomainFailure(
                    FailureCode.BudgetExceeded,
                    "The selected file exceeds the exact approved byte limit."));
            }

            var bytes = File.ReadAllBytes(target.Value.TargetPath);
            cancellationToken.ThrowIfCancellationRequested();
            var encoding = new UTF8Encoding(false, true);
            _ = encoding.GetString(bytes);
            return Bounded(bytes, request.MaximumOutputBytes);
        }
        catch (DecoderFallbackException)
        {
            return Invalid("The selected file is not strict UTF-8 text.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return External("The selected file could not be read safely.");
        }
    }

    private static bool TryWholeNumber(
        IReadOnlyDictionary<string, ToolParameterValue> parameters,
        string name,
        long minimum,
        long maximum,
        out int value)
    {
        value = 0;
        if (!parameters.TryGetValue(name, out var parameter) ||
            parameter.Kind is not ToolParameterValueKind.WholeNumber ||
            parameter.WholeNumber is not { } number || number < minimum || number > maximum)
        {
            return false;
        }

        value = checked((int)number);
        return true;
    }

    private static DomainResult<byte[]> Bounded(byte[] bytes, int maximumBytes) =>
        bytes.Length <= maximumBytes
            ? DomainResult.Success(bytes)
            : DomainResult.Fail<byte[]>(new DomainFailure(
                FailureCode.BudgetExceeded,
                "Built-in tool output exceeds its immutable descriptor limit."));

    private static DomainResult<byte[]> Invalid(string message) =>
        DomainResult.Fail<byte[]>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<byte[]> External(string message) =>
        DomainResult.Fail<byte[]>(new DomainFailure(FailureCode.RecoverableExternalFailure, message, true));
}
