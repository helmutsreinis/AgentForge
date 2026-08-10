using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;

namespace AgentForge.Abstractions.Security;

public interface ILocalAdministratorCredentialService
{
    Task<DomainResult<GeneratedAdministratorCredential>> CreateAsync(
        string logicalName,
        CancellationToken cancellationToken);

    bool Verify(
        ReadOnlySpan<char> credential,
        AdministratorCredentialVerifier verifier);
}

public interface ILocalAdministratorAuthenticator
{
    Task<DomainResult<ActorId>> AuthenticateAsync(
        InstallationId installationId,
        ReadOnlyMemory<char> credential,
        CancellationToken cancellationToken);
}
