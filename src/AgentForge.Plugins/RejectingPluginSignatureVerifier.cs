using AgentForge.Abstractions.Plugins;
using AgentForge.Domain.Plugins;

namespace AgentForge.Plugins;

internal sealed class RejectingPluginSignatureVerifier : IPluginSignatureVerifier
{
    public Task<bool> VerifyAsync(
        PluginManifest manifest,
        ReadOnlyMemory<byte> manifestBytes,
        CancellationToken cancellationToken) => Task.FromResult(false);
}
