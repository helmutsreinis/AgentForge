using System.Security.Cryptography;
using AgentForge.Abstractions.Plugins;
using AgentForge.Domain.Plugins;
using Microsoft.Extensions.Options;

namespace AgentForge.Plugins;

internal sealed class ConfiguredPluginSignatureVerifier(IOptions<PluginOptions> options) : IPluginSignatureVerifier
{
    private const string Algorithm = "ECDSA-P256-SHA256";
    private readonly Dictionary<string, string> _keys = new(
        options.Value.TrustedPublicKeys,
        StringComparer.Ordinal);

    public Task<bool> VerifyAsync(
        PluginManifest manifest,
        ReadOnlyMemory<byte> manifestBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (manifest.Signature is not { } signature || signature.Algorithm != Algorithm ||
            !_keys.TryGetValue(signature.KeyId, out var encodedKey) || manifestBytes.Length is <= 0 or > 1_048_576)
            return Task.FromResult(false);
        try
        {
            var key = Convert.FromBase64String(encodedKey);
            var signatureBytes = Convert.FromBase64String(signature.Value);
            if (key.Length is < 64 or > 512 || signatureBytes.Length is < 64 or > 256)
                return Task.FromResult(false);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key, out var read);
            if (read != key.Length || ecdsa.KeySize != 256) return Task.FromResult(false);
            return Task.FromResult(ecdsa.VerifyData(
                PluginManifestValidator.CreateSigningPayload(manifest),
                signatureBytes,
                HashAlgorithmName.SHA256));
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return Task.FromResult(false);
        }
    }

    internal static bool ValidateKeys(Dictionary<string, string>? keys)
    {
        if (keys is null || keys.Count > 64 || keys.Any(item => !IsKeyId(item.Key))) return false;
        foreach (var encoded in keys.Values)
        {
            try
            {
                var bytes = Convert.FromBase64String(encoded);
                if (bytes.Length is < 64 or > 512) return false;
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(bytes, out var read);
                if (read != bytes.Length || ecdsa.KeySize != 256) return false;
            }
            catch (Exception exception) when (exception is FormatException or CryptographicException)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsKeyId(string value) => value.Length is >= 3 and <= 128 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
}
