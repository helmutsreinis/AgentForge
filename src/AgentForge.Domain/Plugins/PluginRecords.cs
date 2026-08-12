using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Plugins;

public readonly record struct PluginId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct PluginVersion(string Value)
{
    public override string ToString() => Value;
}

public enum PluginRisk
{
    Low,
    Medium,
    High,
}

public enum PluginIsolation
{
    InProcess,
    OutOfProcess,
}

public sealed record PluginSignature(string Algorithm, string KeyId, string Value);

public sealed record PluginManifest(
    int SchemaVersion,
    PluginId Id,
    PluginVersion Version,
    string EntryAssembly,
    string EntryType,
    string AssemblyHash,
    PluginRisk Risk,
    IReadOnlyList<string> Permissions,
    PluginSignature? Signature);

public sealed record PluginDescriptor(
    PluginManifest Manifest,
    string PackageDirectory,
    string AssemblyPath,
    string ManifestHash,
    bool SignatureVerified,
    PluginIsolation Isolation);

public sealed record PluginLoadPlan(
    PluginId Id,
    PluginVersion Version,
    PluginIsolation Isolation,
    IReadOnlyList<string> Permissions,
    string AssemblyHash,
    string ManifestHash);

public sealed record PluginWorkerRequest(
    int ProtocolVersion,
    PluginId PluginId,
    PluginVersion PluginVersion,
    string AssemblyPath,
    string AssemblyHash,
    string EntryType,
    IReadOnlyList<string> Permissions,
    bool NetworkAllowed,
    string? WorkspacePath);

public sealed record PluginWorkerReceipt(
    int ProtocolVersion,
    bool Accepted,
    PluginId PluginId,
    PluginVersion PluginVersion,
    string AssemblyHash);

public static class PluginManifestValidator
{
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    public static DomainResult<bool> Validate(PluginManifest? value)
    {
        if (value is null || value.SchemaVersion != 1 || !IsId(value.Id.Value) ||
            !IsVersion(value.Version.Value) || !IsRelativeFile(value.EntryAssembly, ".dll") ||
            !IsType(value.EntryType) || !IsHash(value.AssemblyHash) || !Enum.IsDefined(value.Risk) ||
            value.Permissions is null || value.Permissions.Count > 64 ||
            value.Permissions.Any(permission => !IsPermission(permission)) ||
            value.Permissions.Distinct(StringComparer.Ordinal).Count() != value.Permissions.Count ||
            value.Signature is { } signature && (!IsText(signature.Algorithm, 64) ||
                !IsText(signature.KeyId, 256) || !IsText(signature.Value, 4096)))
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.ValidationFailure, "Plugin manifest is invalid or exceeds a security bound."));
        return DomainResult.Success(true);
    }

    public static string Hash(ReadOnlySpan<byte> bytes) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";

    public static byte[] CreateSigningPayload(PluginManifest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", value.SchemaVersion);
            writer.WriteString("id", value.Id.Value);
            writer.WriteString("version", value.Version.Value);
            writer.WriteString("entryAssembly", value.EntryAssembly);
            writer.WriteString("entryType", value.EntryType);
            writer.WriteString("assemblyHash", value.AssemblyHash);
            writer.WriteString("risk", value.Risk.ToString());
            writer.WriteStartArray("permissions");
            foreach (var permission in value.Permissions) writer.WriteStringValue(permission);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static bool IsId(string value) => value.Length is >= 3 and <= 128 &&
        char.IsAsciiLetterOrDigit(value[0]) && value.All(character =>
            character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) || character is '.' or '-');

    private static bool IsVersion(string value)
    {
        var parts = value.Split('.', StringSplitOptions.None);
        return parts.Length == 3 && parts.All(part => part.Length > 0 && part.Length <= 9 &&
            part.All(char.IsAsciiDigit) && (part.Length == 1 || part[0] != '0'));
    }

    private static bool IsRelativeFile(string value, string extension) => IsText(value, 256) &&
        !Path.IsPathRooted(value) && string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) &&
        string.Equals(Path.GetExtension(value), extension, StringComparison.OrdinalIgnoreCase);

    private static bool IsType(string value) => IsText(value, 512) && value.Split('.').All(part =>
        part.Length > 0 && (char.IsAsciiLetter(part[0]) || part[0] == '_') &&
        part.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'));

    private static bool IsPermission(string value) => IsText(value, 256) && value.Contains(':') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is ':' or '.' or '-' or '_');

    private static bool IsText(string value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(char.IsControl);

    private static bool IsHash(string value) => value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept(LowerHex) < 0;
}
