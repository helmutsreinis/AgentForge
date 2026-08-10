using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Security;

public readonly record struct AdministratorIdentityId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public sealed record AdministratorCredentialVerifier(
    string Algorithm,
    int WorkFactor,
    string Salt,
    string Verifier);

public sealed record LocalAdministrator(
    AdministratorIdentityId Id,
    InstallationId InstallationId,
    ActorId ActorId,
    SecretReference ClientCredentialReference,
    AdministratorCredentialVerifier CredentialVerifier,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    CorrelationId CorrelationId);

public sealed record GeneratedAdministratorCredential(
    SecretReference ClientCredentialReference,
    AdministratorCredentialVerifier CredentialVerifier);

public sealed record SetupValidationCheck(
    string CheckId,
    bool Succeeded,
    string Summary);

public sealed record SetupCompletionReport(
    InstallationSnapshot Installation,
    LocalAdministrator Administrator,
    IReadOnlyList<SetupValidationCheck> Checks);

public sealed record CompleteSetupRequest(
    ActorId ActorId,
    CorrelationId CorrelationId,
    ReadOnlyMemory<char> AdministratorCredential = default);
