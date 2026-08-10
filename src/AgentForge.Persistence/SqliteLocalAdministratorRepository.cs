using AgentForge.Abstractions.Security;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteLocalAdministratorRepository(AgentForgeDbContext dbContext)
    : ILocalAdministratorRepository
{
    public async ValueTask AddAsync(LocalAdministrator administrator, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        await dbContext.LocalAdministrators.AddAsync(Map(administrator), cancellationToken);
    }

    public async ValueTask<LocalAdministrator?> FindAsync(
        InstallationId installationId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.LocalAdministrators
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.InstallationId == installationId.Value, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    private static LocalAdministratorEntity Map(LocalAdministrator administrator) => new()
    {
        Id = administrator.Id.Value,
        InstallationId = administrator.InstallationId.Value,
        ActorId = administrator.ActorId.Value,
        SecretStore = administrator.ClientCredentialReference.Store,
        SecretKey = administrator.ClientCredentialReference.Key,
        VerifierAlgorithm = administrator.CredentialVerifier.Algorithm,
        VerifierWorkFactor = administrator.CredentialVerifier.WorkFactor,
        VerifierSalt = administrator.CredentialVerifier.Salt,
        Verifier = administrator.CredentialVerifier.Verifier,
        Version = administrator.Version,
        CreatedAt = administrator.CreatedAt,
        UpdatedAt = administrator.UpdatedAt,
        CorrelationId = administrator.CorrelationId.Value,
    };

    private static LocalAdministrator Map(LocalAdministratorEntity entity) => new(
        new AdministratorIdentityId(entity.Id),
        new InstallationId(entity.InstallationId),
        new ActorId(entity.ActorId),
        new SecretReference(entity.SecretStore, entity.SecretKey),
        new AdministratorCredentialVerifier(
            entity.VerifierAlgorithm,
            entity.VerifierWorkFactor,
            entity.VerifierSalt,
            entity.Verifier),
        entity.Version,
        entity.CreatedAt,
        entity.UpdatedAt,
        new CorrelationId(entity.CorrelationId));
}
