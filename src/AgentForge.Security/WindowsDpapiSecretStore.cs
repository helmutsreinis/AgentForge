using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;
using Microsoft.Extensions.Options;

namespace AgentForge.Security;

[SupportedOSPlatform("windows")]
internal sealed class WindowsDpapiSecretStore(
    IDataDirectoryProvider dataDirectoryProvider,
    IIdentifierGenerator identifiers,
    IOptions<SecurityOptions> options) : ISecretStore
{
    public const string Name = "windows-dpapi-current-user";

    public string StoreName => Name;

    public SecretStoreCapability GetCapability() => new(Name, true, null);

    public async Task<DomainResult<SecretReference>> StoreAsync(
        string logicalName,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(logicalName) || secret.IsEmpty || secret.Length > options.Value.MaximumSecretCharacters)
        {
            return Invalid<SecretReference>("Secret name and bounded secret content are required.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var key = identifiers.NewGuid().ToString("D");
        var path = GetSecretPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{identifiers.NewGuid():N}.partial";
        var plaintext = new byte[Encoding.UTF8.GetByteCount(secret.Span)];
        try
        {
            Encoding.UTF8.GetBytes(secret.Span, plaintext);
            var protectedBytes = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken);
                File.Move(temporaryPath, path);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            return DomainResult.Success(new SecretReference(Name, key));
        }
        catch (IOException)
        {
            return External<SecretReference>("The Windows secret could not be stored.");
        }
        catch (CryptographicException)
        {
            return External<SecretReference>("Windows user-scoped protection failed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<DomainResult<SecretLease>> MaterializeAsync(
        SecretReference secretReference,
        CancellationToken cancellationToken)
    {
        var referenceFailure = ValidateReference(secretReference);
        if (referenceFailure is not null)
        {
            return DomainResult.Fail<SecretLease>(referenceFailure);
        }

        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(GetSecretPath(secretReference.Key), cancellationToken);
            try
            {
                var plaintext = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                try
                {
                    return DomainResult.Success(new SecretLease(Encoding.UTF8.GetChars(plaintext)));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
        catch (Exception exception) when (exception is IOException or CryptographicException)
        {
            return External<SecretLease>("The Windows secret could not be materialized.");
        }
    }

    public Task<DomainResult<bool>> DeleteAsync(
        SecretReference secretReference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var referenceFailure = ValidateReference(secretReference);
        if (referenceFailure is not null)
        {
            return Task.FromResult(DomainResult.Fail<bool>(referenceFailure));
        }

        try
        {
            var path = GetSecretPath(secretReference.Key);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return Task.FromResult(DomainResult.Success(true));
        }
        catch (IOException)
        {
            return Task.FromResult(External<bool>("The Windows secret could not be deleted."));
        }
    }

    private string GetSecretPath(string key)
    {
        if (!Guid.TryParseExact(key, "D", out var parsed) || parsed == Guid.Empty)
        {
            throw new ArgumentException("Secret key is not canonical.", nameof(key));
        }

        var dataRoot = Path.GetFullPath(dataDirectoryProvider.GetDataDirectory());
        var secretRoot = Path.GetFullPath(Path.Combine(dataRoot, options.Value.SecretDirectoryName));
        if (!secretRoot.StartsWith(dataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Secret storage must remain within the AgentForge data directory.");
        }

        return Path.Combine(secretRoot, $"{key}.dpapi");
    }

    private static DomainFailure? ValidateReference(SecretReference secretReference)
    {
        if (!string.Equals(secretReference.Store, Name, StringComparison.Ordinal) ||
            !Guid.TryParseExact(secretReference.Key, "D", out var key) || key == Guid.Empty)
        {
            return new DomainFailure(FailureCode.ValidationFailure, "Secret reference does not belong to this store.");
        }

        return null;
    }

    private static DomainResult<T> Invalid<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<T> External<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.RecoverableExternalFailure, message, IsRetryable: true));
}
