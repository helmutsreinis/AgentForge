using System.Reflection;
using System.Runtime.Loader;
using AgentForge.Abstractions.Plugins;
using AgentForge.Domain.Plugins;
using AgentForge.Domain.Primitives;

namespace AgentForge.Plugins;

internal sealed class PluginLoader(IPluginWorkerLauncher workerLauncher) : IPluginLoader
{
    public DomainResult<PluginLoadPlan> Plan(PluginDescriptor descriptor)
    {
        var validation = PluginManifestValidator.Validate(descriptor.Manifest);
        if (!validation.IsSuccess || descriptor.Isolation == PluginIsolation.InProcess &&
            (!descriptor.SignatureVerified || descriptor.Manifest.Risk != PluginRisk.Low))
            return DomainResult.Fail<PluginLoadPlan>(new DomainFailure(
                FailureCode.PolicyDenied, "In-process plugins require a verified signature and low risk."));
        return DomainResult.Success(new PluginLoadPlan(
            descriptor.Manifest.Id, descriptor.Manifest.Version, descriptor.Isolation,
            descriptor.Manifest.Permissions.ToArray(), descriptor.Manifest.AssemblyHash, descriptor.ManifestHash));
    }

    public async Task<DomainResult<IPluginHandle>> LoadAsync(
        PluginDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var planned = Plan(descriptor);
        if (!planned.IsSuccess) return DomainResult.Fail<IPluginHandle>(planned.Failure!);
        var currentHash = PluginManifestValidator.Hash(await File.ReadAllBytesAsync(
            descriptor.AssemblyPath, cancellationToken));
        if (!string.Equals(currentHash, descriptor.Manifest.AssemblyHash, StringComparison.Ordinal))
            return DomainResult.Fail<IPluginHandle>(new DomainFailure(
                FailureCode.ConcurrencyConflict, "Plugin assembly changed after discovery."));
        if (planned.Value.Isolation == PluginIsolation.OutOfProcess)
        {
            var request = new PluginWorkerRequest(
                1, planned.Value.Id, planned.Value.Version, descriptor.AssemblyPath,
                planned.Value.AssemblyHash, descriptor.Manifest.EntryType,
                planned.Value.Permissions, false, null);
            return await workerLauncher.LaunchAsync(planned.Value, request, cancellationToken);
        }
        try
        {
            var context = new PluginLoadContext(descriptor.AssemblyPath);
            var assembly = context.LoadFromAssemblyPath(descriptor.AssemblyPath);
            var type = assembly.GetType(descriptor.Manifest.EntryType, throwOnError: false, ignoreCase: false);
            if (type is null || !typeof(IAgentForgePlugin).IsAssignableFrom(type) || type.IsAbstract ||
                type.GetConstructor(Type.EmptyTypes) is null)
            {
                context.Unload();
                return DomainResult.Fail<IPluginHandle>(new DomainFailure(
                    FailureCode.ValidationFailure, "Plugin entry type does not implement the SDK contract."));
            }
            var plugin = (IAgentForgePlugin)Activator.CreateInstance(type)!;
            if (plugin.Id != planned.Value.Id || plugin.Version != planned.Value.Version)
            {
                context.Unload();
                return DomainResult.Fail<IPluginHandle>(new DomainFailure(
                    FailureCode.ValidationFailure, "Loaded plugin identity does not match its signed manifest."));
            }
            return DomainResult.Success<IPluginHandle>(new InProcessPluginHandle(planned.Value, context, plugin));
        }
        catch (Exception exception) when (exception is FileLoadException or BadImageFormatException or
            TypeLoadException or TargetInvocationException or MemberAccessException)
        {
            return DomainResult.Fail<IPluginHandle>(new DomainFailure(
                FailureCode.ValidationFailure, "The verified plugin could not be loaded."));
        }
    }

    private sealed class PluginLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is "AgentForge.Domain" or "AgentForge.Abstractions") return null;
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }

    private sealed class InProcessPluginHandle(
        PluginLoadPlan plan,
        PluginLoadContext context,
        IAgentForgePlugin instance) : IPluginHandle
    {
        public PluginLoadPlan Plan { get; } = plan;

        public async ValueTask DisposeAsync()
        {
            if (instance is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (instance is IDisposable disposable) disposable.Dispose();
            context.Unload();
        }
    }
}
