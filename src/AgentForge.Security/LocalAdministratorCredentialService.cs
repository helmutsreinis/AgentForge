using System.Security.Cryptography;
using AgentForge.Abstractions.Security;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;

namespace AgentForge.Security;

internal sealed class LocalAdministratorCredentialService(ISecretStore secretStore)
    : ILocalAdministratorCredentialService
{
    private const string Algorithm = "PBKDF2-SHA256";
    private const int WorkFactor = 600_000;
    private const int CredentialByteCount = 32;
    private const int SaltByteCount = 16;
    private const int VerifierByteCount = 32;

    public async Task<DomainResult<GeneratedAdministratorCredential>> CreateAsync(
        string logicalName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(logicalName))
        {
            return DomainResult.Fail<GeneratedAdministratorCredential>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Administrator credential name is required."));
        }

        var randomBytes = new byte[CredentialByteCount];
        var credential = new char[CredentialByteCount * 2];
        var salt = new byte[SaltByteCount];
        var verifierBytes = new byte[VerifierByteCount];
        try
        {
            RandomNumberGenerator.Fill(randomBytes);
            RandomNumberGenerator.Fill(salt);
            EncodeHex(randomBytes, credential);
            Rfc2898DeriveBytes.Pbkdf2(
                credential,
                salt,
                verifierBytes,
                WorkFactor,
                HashAlgorithmName.SHA256);
            var stored = await secretStore.StoreAsync(logicalName, credential, cancellationToken);
            if (!stored.IsSuccess)
            {
                return DomainResult.Fail<GeneratedAdministratorCredential>(stored.Failure!);
            }

            return DomainResult.Success(new GeneratedAdministratorCredential(
                stored.Value,
                new AdministratorCredentialVerifier(
                    Algorithm,
                    WorkFactor,
                    Convert.ToBase64String(salt),
                    Convert.ToBase64String(verifierBytes))));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(randomBytes);
            Array.Clear(credential);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(verifierBytes);
        }
    }

    public bool Verify(
        ReadOnlySpan<char> credential,
        AdministratorCredentialVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        if (!string.Equals(verifier.Algorithm, Algorithm, StringComparison.Ordinal) ||
            verifier.WorkFactor != WorkFactor ||
            credential.IsEmpty ||
            credential.Length > 256)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(verifier.Salt);
            expected = Convert.FromBase64String(verifier.Verifier);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = new byte[expected.Length];
        try
        {
            if (salt.Length != SaltByteCount || expected.Length != VerifierByteCount)
            {
                return false;
            }

            Rfc2898DeriveBytes.Pbkdf2(
                credential,
                salt,
                actual,
                WorkFactor,
                HashAlgorithmName.SHA256);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    private static void EncodeHex(ReadOnlySpan<byte> source, Span<char> destination)
    {
        const string alphabet = "0123456789abcdef";
        for (var index = 0; index < source.Length; index++)
        {
            destination[index * 2] = alphabet[source[index] >> 4];
            destination[(index * 2) + 1] = alphabet[source[index] & 0x0f];
        }
    }
}
