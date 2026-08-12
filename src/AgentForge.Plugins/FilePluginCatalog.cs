using System.Text.Json;
using AgentForge.Abstractions.Plugins;
using AgentForge.Domain.Plugins;
using AgentForge.Domain.Primitives;
using Microsoft.Extensions.Options;

namespace AgentForge.Plugins;

internal sealed class FilePluginCatalog(
    IOptions<PluginOptions> options,
    IPluginSignatureVerifier signatureVerifier) : IPluginCatalog
{
    private const string ManifestName = "plugin.harness.json";
    private static readonly HashSet<string> Properties = new(StringComparer.Ordinal)
    {
        "schemaVersion", "id", "version", "entryAssembly", "entryType", "assemblyHash", "risk",
        "permissions", "signature",
    };

    public async Task<DomainResult<IReadOnlyList<PluginDescriptor>>> DiscoverAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(options.Value.Directory);
        if (!Directory.Exists(root)) return DomainResult.Success<IReadOnlyList<PluginDescriptor>>([]);
        var packages = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal).Take(options.Value.MaximumPackages + 1).ToArray();
        if (packages.Length > options.Value.MaximumPackages)
            return Failure("Plugin catalog exceeds the configured package bound.");
        var descriptors = new List<PluginDescriptor>(packages.Length);
        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new DirectoryInfo(package);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                return Failure("Plugin package directories cannot be filesystem links.");
            var path = Path.Combine(package, ManifestName);
            if (!File.Exists(path)) return Failure("Every plugin package directory requires plugin.harness.json.");
            var loaded = await LoadAsync(package, path, cancellationToken);
            if (!loaded.IsSuccess) return DomainResult.Fail<IReadOnlyList<PluginDescriptor>>(loaded.Failure!);
            descriptors.Add(loaded.Value);
        }
        if (descriptors.Select(item => (item.Manifest.Id, item.Manifest.Version)).Distinct().Count() != descriptors.Count)
            return Failure("Plugin catalog contains a duplicate id and version.");
        return DomainResult.Success<IReadOnlyList<PluginDescriptor>>(descriptors);
    }

    private async Task<DomainResult<PluginDescriptor>> LoadAsync(
        string root, string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!string.Equals(info.DirectoryName, root, StringComparison.OrdinalIgnoreCase) ||
            info.Length is <= 0 || info.Length > options.Value.MaximumManifestBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
            return DomainResult.Fail<PluginDescriptor>(Invalid("Plugin manifest path or size is invalid."));
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (!TryParse(bytes, out var manifest) || !PluginManifestValidator.Validate(manifest).IsSuccess)
            return DomainResult.Fail<PluginDescriptor>(Invalid("Plugin manifest schema or content is invalid."));
        var assemblyPath = Path.GetFullPath(Path.Combine(root, manifest!.EntryAssembly));
        if (!string.Equals(Path.GetDirectoryName(assemblyPath), root, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(assemblyPath))
            return DomainResult.Fail<PluginDescriptor>(Invalid("Plugin assembly must be a direct package file."));
        var assembly = new FileInfo(assemblyPath);
        if (assembly.Length is <= 0 || assembly.Length > options.Value.MaximumAssemblyBytes ||
            (assembly.Attributes & FileAttributes.ReparsePoint) != 0)
            return DomainResult.Fail<PluginDescriptor>(Invalid("Plugin assembly path or size is invalid."));
        var assemblyBytes = await File.ReadAllBytesAsync(assemblyPath, cancellationToken);
        if (!string.Equals(PluginManifestValidator.Hash(assemblyBytes), manifest.AssemblyHash, StringComparison.Ordinal))
            return DomainResult.Fail<PluginDescriptor>(Invalid("Plugin assembly hash does not match the manifest."));
        var verified = manifest.Signature is not null &&
            await signatureVerifier.VerifyAsync(manifest, bytes, cancellationToken);
        var isolation = verified && manifest.Risk == PluginRisk.Low
            ? PluginIsolation.InProcess
            : PluginIsolation.OutOfProcess;
        return DomainResult.Success(new PluginDescriptor(
            manifest, root, assemblyPath, PluginManifestValidator.Hash(bytes), verified, isolation));
    }

    private static bool TryParse(byte[] bytes, out PluginManifest? manifest)
    {
        manifest = null;
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            using var document = JsonDocument.ParseValue(ref reader);
            if (reader.Read() || document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!names.Add(property.Name) || !Properties.Contains(property.Name)) return false;
            if (!Properties.SetEquals(names)) return false;
            var root = document.RootElement;
            var riskText = root.GetProperty("risk").GetString();
            if (!Enum.TryParse<PluginRisk>(riskText, false, out var risk) || !Enum.IsDefined(risk)) return false;
            var permissions = root.GetProperty("permissions").EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty).ToArray();
            PluginSignature? signature = null;
            var signatureElement = root.GetProperty("signature");
            if (signatureElement.ValueKind != JsonValueKind.Null)
            {
                if (signatureElement.ValueKind != JsonValueKind.Object) return false;
                var signatureNames = signatureElement.EnumerateObject().Select(item => item.Name).ToArray();
                if (signatureNames.Length != 3 || signatureNames.Distinct(StringComparer.Ordinal).Count() != 3 ||
                    !signatureNames.ToHashSet(StringComparer.Ordinal).SetEquals(["algorithm", "keyId", "value"]))
                    return false;
                signature = new PluginSignature(
                    signatureElement.GetProperty("algorithm").GetString() ?? string.Empty,
                    signatureElement.GetProperty("keyId").GetString() ?? string.Empty,
                    signatureElement.GetProperty("value").GetString() ?? string.Empty);
            }
            manifest = new PluginManifest(
                root.GetProperty("schemaVersion").GetInt32(),
                new PluginId(root.GetProperty("id").GetString() ?? string.Empty),
                new PluginVersion(root.GetProperty("version").GetString() ?? string.Empty),
                root.GetProperty("entryAssembly").GetString() ?? string.Empty,
                root.GetProperty("entryType").GetString() ?? string.Empty,
                root.GetProperty("assemblyHash").GetString() ?? string.Empty,
                risk, permissions, signature);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static DomainResult<IReadOnlyList<PluginDescriptor>> Failure(string message) =>
        DomainResult.Fail<IReadOnlyList<PluginDescriptor>>(Invalid(message));

    private static DomainFailure Invalid(string message) =>
        new(FailureCode.ValidationFailure, message);
}
