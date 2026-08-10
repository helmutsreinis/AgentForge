using AgentForge.Abstractions.Security;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;

namespace AgentForge.Security;

internal sealed class UnavailableSecretStore : ISecretStore
{
    private static readonly DomainFailure Failure = new(
        FailureCode.UnsupportedCapability,
        "No OS-backed secret store is available on this platform.");

    public string StoreName => "unavailable";

    public SecretStoreCapability GetCapability() => new(StoreName, false, Failure);

    public Task<DomainResult<SecretReference>> StoreAsync(
        string logicalName,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken) => Task.FromResult(DomainResult.Fail<SecretReference>(Failure));

    public Task<DomainResult<SecretLease>> MaterializeAsync(
        SecretReference secretReference,
        CancellationToken cancellationToken) => Task.FromResult(DomainResult.Fail<SecretLease>(Failure));

    public Task<DomainResult<bool>> DeleteAsync(
        SecretReference secretReference,
        CancellationToken cancellationToken) => Task.FromResult(DomainResult.Fail<bool>(Failure));
}
