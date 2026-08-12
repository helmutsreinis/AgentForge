using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Plugins;
using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Plugins;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Tools;
using Microsoft.Extensions.Options;

namespace AgentForge.Plugins;

internal sealed class SandboxPluginWorkerLauncher(
    ISandbox sandbox,
    IOptions<PluginOptions> options) : IPluginWorkerLauncher
{
    private const ProcessIsolationFeature Required = ProcessIsolationFeature.DirectExecutable |
        ProcessIsolationFeature.ArgumentArray | ProcessIsolationFeature.EnvironmentAllowlist |
        ProcessIsolationFeature.WorkingDirectoryContainment | ProcessIsolationFeature.BoundedOutput |
        ProcessIsolationFeature.WallClockTimeout | ProcessIsolationFeature.ProcessTreeTermination |
        ProcessIsolationFeature.KillOnControllerExit | ProcessIsolationFeature.NetworkIsolation |
        ProcessIsolationFeature.FileSystemIsolation | ProcessIsolationFeature.CpuLimit |
        ProcessIsolationFeature.MemoryLimit | ProcessIsolationFeature.ProcessLimit;

    public async Task<DomainResult<IPluginHandle>> LaunchAsync(
        PluginLoadPlan plan,
        PluginWorkerRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.PluginWorkerExecutable))
            return Unsupported("The constrained plugin worker executable is not configured.");
        var executable = Path.GetFullPath(options.Value.PluginWorkerExecutable);
        if (!File.Exists(executable)) return Unsupported("The constrained plugin worker executable is unavailable.");
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request));
        if (payload.Length > 262_144) return Unsupported("The plugin worker request exceeds the protocol bound.");
        var package = Path.GetDirectoryName(request.AssemblyPath)!;
        var result = await sandbox.ExecuteAsync(new ProcessExecutionRequest(
            executable,
            ["--request-base64", payload],
            package,
            package,
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(30),
            16_384,
            ProcessNetworkPolicy.Denied,
            ProcessSandboxKind.Container,
            Required), null, cancellationToken);
        if (!result.IsSuccess) return DomainResult.Fail<IPluginHandle>(result.Failure!);
        if (result.Value.ExitCode != 0 || result.Value.StandardError.Length != 0 ||
            !TryReadReceipt(result.Value.StandardOutput, request, out var receipt))
            return DomainResult.Fail<IPluginHandle>(new DomainFailure(
                FailureCode.RecoverableExternalFailure, "The isolated plugin worker rejected its pinned request."));
        return DomainResult.Success<IPluginHandle>(new WorkerPluginHandle(plan, receipt!));
    }

    private static bool TryReadReceipt(
        byte[] bytes,
        PluginWorkerRequest request,
        out PluginWorkerReceipt? receipt)
    {
        receipt = null;
        if (bytes.Length is <= 0 or > 16_384) return false;
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\r', '\n');
            receipt = JsonSerializer.Deserialize<PluginWorkerReceipt>(text);
            return receipt is not null && receipt.ProtocolVersion == 1 && receipt.Accepted &&
                receipt.PluginId == request.PluginId && receipt.PluginVersion == request.PluginVersion &&
                string.Equals(receipt.AssemblyHash, request.AssemblyHash, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static DomainResult<IPluginHandle> Unsupported(string message) =>
        DomainResult.Fail<IPluginHandle>(new DomainFailure(FailureCode.UnsupportedCapability, message));

    private sealed class WorkerPluginHandle(PluginLoadPlan plan, PluginWorkerReceipt receipt) : IPluginHandle
    {
        public PluginLoadPlan Plan { get; } = plan;

        public ValueTask DisposeAsync()
        {
            GC.KeepAlive(receipt);
            return ValueTask.CompletedTask;
        }
    }
}
