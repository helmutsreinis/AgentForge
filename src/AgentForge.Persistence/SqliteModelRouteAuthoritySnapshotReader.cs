using System.Collections.ObjectModel;
using System.Data;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Providers;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteModelRouteAuthoritySnapshotReader(
    AgentForgeDbContext dbContext,
    IInstallationRepository installations,
    IAgentIdentityRepository agents,
    IProviderProfileRepository providers) : IModelRouteAuthoritySnapshotReader
{
    public async Task<DomainResult<ModelRouteAuthoritySnapshot>> ReadAsync(
        InstallationId installationId,
        AgentIdentityId agentId,
        CancellationToken cancellationToken)
    {
        if (installationId.Value == Guid.Empty || agentId.Value == Guid.Empty)
        {
            return Invalid();
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var installation = await installations.ReadAsync(cancellationToken);
        if (installation.Id != installationId)
        {
            await transaction.CommitAsync(cancellationToken);
            return Denied();
        }

        var agent = await agents.FindByIdAsync(agentId, cancellationToken);
        if (agent is null || agent.InstallationId != installationId)
        {
            await transaction.CommitAsync(cancellationToken);
            return Denied();
        }

        var profiles = await providers.ListAsync(installationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return DomainResult.Success(new ModelRouteAuthoritySnapshot(
            installation,
            agent,
            new ReadOnlyCollection<AgentForge.Domain.Providers.ProviderProfile>(profiles.ToArray())));
    }

    private static DomainResult<ModelRouteAuthoritySnapshot> Invalid() =>
        DomainResult.Fail<ModelRouteAuthoritySnapshot>(new DomainFailure(
            FailureCode.ValidationFailure,
            "Model route authority requires non-empty installation and agent identities."));

    private static DomainResult<ModelRouteAuthoritySnapshot> Denied() =>
        DomainResult.Fail<ModelRouteAuthoritySnapshot>(new DomainFailure(
            FailureCode.PolicyDenied,
            "The requested model route authority is unavailable in this installation."));
}
