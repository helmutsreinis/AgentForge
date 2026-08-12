using AgentForge.Abstractions.Plugins;
using AgentForge.Abstractions.Setup;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Setup;

namespace AgentForge.Plugins;

internal sealed class PluginRecoveryConfigurationInspector(IPluginCatalog catalog)
    : IRecoveryConfigurationInspector
{
    public async Task<DoctorCheck> InspectAsync(
        InstallationId installationId,
        CancellationToken cancellationToken)
    {
        _ = installationId;
        try
        {
            var discovered = await catalog.DiscoverAsync(cancellationToken);
            return discovered.IsSuccess
                ? new DoctorCheck(
                    "plugin.catalog",
                    DoctorCheckStatus.Pass,
                    $"Validated {discovered.Value.Count} inert plugin package(s) without loading assemblies.")
                : Failure($"Plugin catalog validation failed: {discovered.Failure!.Message}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure("Plugin catalog storage is unavailable or inaccessible.");
        }
    }

    private static DoctorCheck Failure(string summary) =>
        new("plugin.catalog", DoctorCheckStatus.Fail, summary);
}
