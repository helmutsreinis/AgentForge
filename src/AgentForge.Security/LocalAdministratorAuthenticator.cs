using AgentForge.Abstractions.Security;
using AgentForge.Domain.Primitives;

namespace AgentForge.Security;

internal sealed class LocalAdministratorAuthenticator(
    ILocalAdministratorRepository administrators,
    ILocalAdministratorCredentialService credentials) : ILocalAdministratorAuthenticator
{
    public async Task<DomainResult<ActorId>> AuthenticateAsync(
        InstallationId installationId,
        ReadOnlyMemory<char> credential,
        CancellationToken cancellationToken)
    {
        if (installationId.Value == Guid.Empty || credential.IsEmpty || credential.Length > 256)
        {
            return Denied();
        }

        var administrator = await administrators.FindAsync(installationId, cancellationToken);
        return administrator is not null && credentials.Verify(credential.Span, administrator.CredentialVerifier)
            ? DomainResult.Success(administrator.ActorId)
            : Denied();
    }

    private static DomainResult<ActorId> Denied() => DomainResult.Fail<ActorId>(new DomainFailure(
        FailureCode.PolicyDenied,
        "Administrator authentication failed."));
}
