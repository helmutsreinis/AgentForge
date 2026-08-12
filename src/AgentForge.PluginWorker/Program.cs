using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using AgentForge.Abstractions.Plugins;
using AgentForge.Domain.Plugins;

if (args is not ["--request-base64", var payload] || payload.Length is <= 0 or > 262_144)
    return 2;

PluginWorkerRequest? request;
try
{
    var bytes = Convert.FromBase64String(payload);
    request = JsonSerializer.Deserialize<PluginWorkerRequest>(bytes);
}
catch (Exception exception) when (exception is FormatException or JsonException)
{
    return 2;
}

if (request is null || request.ProtocolVersion != 1 || request.NetworkAllowed ||
    request.WorkspacePath is not null || !PluginManifestValidator.Validate(new PluginManifest(
        1, request.PluginId, request.PluginVersion, Path.GetFileName(request.AssemblyPath), request.EntryType,
        request.AssemblyHash, PluginRisk.High, request.Permissions, null)).IsSuccess)
    return 3;

var path = Path.GetFullPath(request.AssemblyPath);
if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
    !string.Equals(PluginManifestValidator.Hash(await File.ReadAllBytesAsync(path)),
        request.AssemblyHash, StringComparison.Ordinal))
    return 3;

var context = new WorkerLoadContext(path);
try
{
    var assembly = context.LoadFromAssemblyPath(path);
    var type = assembly.GetType(request.EntryType, throwOnError: false, ignoreCase: false);
    if (type is null || !typeof(IAgentForgePlugin).IsAssignableFrom(type) || type.IsAbstract ||
        type.GetConstructor(Type.EmptyTypes) is null)
        return 3;
    var plugin = (IAgentForgePlugin)Activator.CreateInstance(type)!;
    if (plugin.Id != request.PluginId || plugin.Version != request.PluginVersion) return 3;
    if (plugin is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
    else if (plugin is IDisposable disposable) disposable.Dispose();
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new PluginWorkerReceipt(
        1, true, request.PluginId, request.PluginVersion, request.AssemblyHash)));
    return 0;
}
catch (Exception exception) when (exception is FileLoadException or BadImageFormatException or
    TypeLoadException or TargetInvocationException or MemberAccessException)
{
    return 3;
}
finally
{
    context.Unload();
}

internal sealed class WorkerLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is "AgentForge.Domain" or "AgentForge.Abstractions") return null;
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
