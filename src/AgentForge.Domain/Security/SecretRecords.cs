using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Security;

public sealed record SecretReference(string Store, string Key)
{
    public const string NoCredentialStore = "agentforge-no-credential";
    public const string NoCredentialKey = "none";

    public static SecretReference NoCredential { get; } = new(NoCredentialStore, NoCredentialKey);

    public bool IsNoCredential =>
        string.Equals(Store, NoCredentialStore, StringComparison.Ordinal) &&
        string.Equals(Key, NoCredentialKey, StringComparison.Ordinal);
}

public sealed class SecretLease : IAsyncDisposable, IDisposable
{
    private char[]? _value;

    public SecretLease(char[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    public ReadOnlyMemory<char> Value => _value is null
        ? throw new ObjectDisposedException(nameof(SecretLease))
        : _value;

    public void Dispose()
    {
        var value = Interlocked.Exchange(ref _value, null);
        if (value is not null)
        {
            Array.Clear(value);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed record SecretStoreCapability(
    string Store,
    bool IsAvailable,
    DomainFailure? UnavailableReason);
