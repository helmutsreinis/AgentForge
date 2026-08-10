using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;

namespace AgentForge.Abstractions.Security;

public interface ISecretStore
{
    string StoreName { get; }

    SecretStoreCapability GetCapability();

    Task<DomainResult<SecretReference>> StoreAsync(
        string logicalName,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken);

    Task<DomainResult<SecretLease>> MaterializeAsync(
        SecretReference secretReference,
        CancellationToken cancellationToken);

    Task<DomainResult<bool>> DeleteAsync(
        SecretReference secretReference,
        CancellationToken cancellationToken);
}
