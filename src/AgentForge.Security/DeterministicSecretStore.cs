using System.Collections.Concurrent;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;

namespace AgentForge.Security;

public sealed class DeterministicSecretStore(IIdentifierGenerator identifiers) : ISecretStore, IDisposable
{
    public const string Name = "deterministic-secret-store";
    private readonly ConcurrentDictionary<string, char[]> _values = new(StringComparer.Ordinal);

    public string StoreName => Name;

    public SecretStoreCapability GetCapability() => new(Name, true, null);

    public Task<DomainResult<SecretReference>> StoreAsync(
        string logicalName,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(logicalName) || secret.IsEmpty)
        {
            return Task.FromResult(DomainResult.Fail<SecretReference>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Secret name and value are required.")));
        }

        var key = identifiers.NewGuid().ToString("D");
        _values[key] = secret.ToArray();
        return Task.FromResult(DomainResult.Success(new SecretReference(Name, key)));
    }

    public Task<DomainResult<SecretLease>> MaterializeAsync(
        SecretReference secretReference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(secretReference.Store, Name, StringComparison.Ordinal) ||
            !_values.TryGetValue(secretReference.Key, out var secret))
        {
            return Task.FromResult(DomainResult.Fail<SecretLease>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Secret reference was not found in the deterministic store.")));
        }

        return Task.FromResult(DomainResult.Success(new SecretLease((char[])secret.Clone())));
    }

    public Task<DomainResult<bool>> DeleteAsync(
        SecretReference secretReference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(secretReference.Store, Name, StringComparison.Ordinal))
        {
            return Task.FromResult(DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Secret reference does not belong to the deterministic store.")));
        }

        if (_values.TryRemove(secretReference.Key, out var removed))
        {
            Array.Clear(removed);
        }

        return Task.FromResult(DomainResult.Success(true));
    }

    public void Dispose()
    {
        foreach (var value in _values.Values)
        {
            Array.Clear(value);
        }

        _values.Clear();
    }
}
