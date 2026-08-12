using AgentForge.Domain.Plugins;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Plugins;

public interface IAgentForgePlugin
{
    PluginId Id { get; }

    PluginVersion Version { get; }
}

public interface IPluginCatalog
{
    Task<DomainResult<IReadOnlyList<PluginDescriptor>>> DiscoverAsync(CancellationToken cancellationToken);
}

public interface IPluginSignatureVerifier
{
    Task<bool> VerifyAsync(
        PluginManifest manifest,
        ReadOnlyMemory<byte> manifestBytes,
        CancellationToken cancellationToken);
}

public interface IPluginHandle : IAsyncDisposable
{
    PluginLoadPlan Plan { get; }
}

public interface IPluginLoader
{
    DomainResult<PluginLoadPlan> Plan(PluginDescriptor descriptor);

    Task<DomainResult<IPluginHandle>> LoadAsync(
        PluginDescriptor descriptor,
        CancellationToken cancellationToken);
}

public interface IPluginWorkerLauncher
{
    Task<DomainResult<IPluginHandle>> LaunchAsync(
        PluginLoadPlan plan,
        PluginWorkerRequest request,
        CancellationToken cancellationToken);
}
