using AgentForge.Abstractions.Installations;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgentForge.Host.Health;

public sealed class InstallationReadinessHealthCheck(IInstallationStateReader stateReader) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var state = await stateReader.ReadAsync(cancellationToken);
        return state.IsReady
            ? HealthCheckResult.Healthy("Installation is ready.", new Dictionary<string, object>
            {
                ["installationState"] = state.State.ToString(),
                ["version"] = state.Version,
            })
            : HealthCheckResult.Unhealthy("Installation setup is incomplete or recovery is required.", data: new Dictionary<string, object>
            {
                ["installationState"] = state.State.ToString(),
                ["version"] = state.Version,
            });
    }
}
