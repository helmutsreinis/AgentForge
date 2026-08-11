using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Skills;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Skills;

namespace AgentForge.Skills;

internal sealed class PortableSkillPackageLoader(ISkillSignatureVerifier signatureVerifier) : ISkillPackageLoader
{
    private const int MaximumFiles = 128;
    private const int MaximumFileBytes = 1_048_576;
    private const int MaximumPackageBytes = 4_194_304;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const string PlaceholderHash =
        "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    public async Task<DomainResult<LoadedSkillPackage>> LoadAsync(
        string packageDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packageDirectory) || packageDirectory.Length > 4_096)
        {
            return Failure("The skill package path is invalid.");
        }

        string root;
        try
        {
            root = Path.GetFullPath(packageDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure("The skill package path is invalid.");
        }

        if (!Directory.Exists(root) || IsReparsePoint(root))
        {
            return Failure("The skill package directory is missing or linked.");
        }

        string[] paths;
        try
        {
            paths = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure("The skill package could not be enumerated safely.");
        }

        if (paths.Length is < 2 or > MaximumFiles ||
            paths.Any(path => IsReparsePoint(path) || HasLinkedParent(root, path)))
        {
            return Failure("The skill package file count or link policy is invalid.");
        }

        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var total = 0;
        try
        {
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                {
                    return Failure("A skill package file escaped its root.");
                }

                var info = new FileInfo(path);
                if (info.Length is < 0 or > MaximumFileBytes || total + info.Length > MaximumPackageBytes)
                {
                    return Failure("A skill package file or total package exceeded its byte bound.");
                }

                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                total = checked(total + bytes.Length);
                files.Add(relative, bytes);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure("The skill package changed or became unreadable during loading.");
        }

        if (!files.TryGetValue("SKILL.md", out var markdownBytes) ||
            !files.TryGetValue("skill.harness.json", out var manifestBytes) ||
            markdownBytes.Length > 262_144 || manifestBytes.Length > 262_144)
        {
            return Failure("The package requires bounded root SKILL.md and skill.harness.json files.");
        }

        string markdown;
        JsonDocument document;
        try
        {
            markdown = StrictUtf8.GetString(markdownBytes);
            _ = StrictUtf8.GetString(manifestBytes);
            if (HasDuplicateJsonProperties(manifestBytes))
            {
                return Failure("The skill manifest contains duplicate JSON properties.");
            }

            document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        }
        catch (Exception exception) when (exception is DecoderFallbackException or JsonException)
        {
            return Failure("The skill Markdown or manifest is not strict UTF-8/JSON.");
        }

        using (document)
        {
            var parsed = ParseManifest(document.RootElement, markdown, files);
            if (!parsed.IsSuccess)
            {
                return DomainResult.Fail<LoadedSkillPackage>(parsed.Failure!);
            }

            var bundle = CreateCanonicalBundle(files);
            var packageHash = Hash(bundle.Span);
            var package = parsed.Value with { PackageHash = packageHash };
            if (package.Signature is { } signature)
            {
                var signingFiles = files.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
                signingFiles["skill.harness.json"] = CreateUnsignedManifest(document.RootElement);
                var signingBundle = CreateCanonicalBundle(new SortedDictionary<string, byte[]>(
                    signingFiles,
                    StringComparer.Ordinal));
                var verified = signatureVerifier.Verify(new SkillSignatureVerificationRequest(
                    Hash(signingBundle.Span),
                    signature.Algorithm,
                    signature.KeyId,
                    signature.Value));
                if (!verified.IsSuccess || !verified.Value)
                {
                    return DomainResult.Fail<LoadedSkillPackage>(verified.Failure ?? new DomainFailure(
                        FailureCode.PolicyDenied,
                        "The skill package signature was not trusted."));
                }
            }

            var validation = SkillPackageValidator.Validate(package);
            return validation.IsSuccess
                ? DomainResult.Success(new LoadedSkillPackage(package, bundle.ToArray()))
                : DomainResult.Fail<LoadedSkillPackage>(validation.Failure!);
        }
    }

    private static DomainResult<SkillPackage> ParseManifest(
        JsonElement root,
        string markdown,
        IReadOnlyDictionary<string, byte[]> files)
    {
        if (root.ValueKind is not JsonValueKind.Object || !HasExactProperties(root,
                "schemaVersion", "id", "version", "description", "dependencies", "requirements",
                "permissions", "signature") ||
            !root.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var schemaVersion) ||
            schemaVersion != 1 || !TryString(root, "id", 256, out var id) ||
            !TryString(root, "version", 128, out var versionText) ||
            !SkillVersion.TryParse(versionText, out var version) ||
            !TryString(root, "description", 2_048, out var description) ||
            !TryDependencies(root, out var dependencies) || !TryRequirements(root, out var requirements) ||
            !TryStringArray(root, "permissions", 128, 256, out var permissions) ||
            !TrySignature(root, out var signature))
        {
            return DomainResult.Fail<SkillPackage>(new DomainFailure(
                FailureCode.ValidationFailure,
                "The skill manifest schema or values are invalid."));
        }

        var hashes = files.ToDictionary(item => item.Key, item => Hash(item.Value), StringComparer.Ordinal);
        return DomainResult.Success(new SkillPackage(
            new SkillId(id!),
            version,
            description!,
            markdown,
            dependencies!,
            requirements!,
            permissions!,
            hashes,
            hashes["skill.harness.json"],
            PlaceholderHash,
            signature));
    }

    private static bool TryDependencies(JsonElement root, out IReadOnlyList<SkillDependency>? dependencies)
    {
        dependencies = null;
        if (!root.TryGetProperty("dependencies", out var element) || element.ValueKind is not JsonValueKind.Array ||
            element.GetArrayLength() > 128)
        {
            return false;
        }

        var result = new List<SkillDependency>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.Object || !HasExactProperties(item, "id", "version") ||
                !TryString(item, "id", 256, out var id) || !TryString(item, "version", 128, out var text) ||
                !SkillVersion.TryParse(text, out var version))
            {
                return false;
            }

            result.Add(new SkillDependency(new SkillId(id!), version));
        }

        dependencies = result;
        return true;
    }

    private static bool TryRequirements(JsonElement root, out SkillRequirements? requirements)
    {
        requirements = null;
        if (!root.TryGetProperty("requirements", out var element) || element.ValueKind is not JsonValueKind.Object ||
            !HasExactProperties(element, "operatingSystems", "modelCapabilities", "tools") ||
            !TryStringArray(element, "operatingSystems", 16, 64, out var systems) ||
            !TryStringArray(element, "modelCapabilities", 128, 256, out var models) ||
            !TryStringArray(element, "tools", 128, 256, out var tools))
        {
            return false;
        }

        requirements = new SkillRequirements(systems!, models!, tools!);
        return true;
    }

    private static bool TrySignature(JsonElement root, out SkillSignature? signature)
    {
        signature = null;
        if (!root.TryGetProperty("signature", out var element) || element.ValueKind is JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind is not JsonValueKind.Object ||
            !HasExactProperties(element, "algorithm", "keyId", "value") ||
            !TryString(element, "algorithm", 64, out var algorithm) ||
            !TryString(element, "keyId", 256, out var keyId) ||
            !TryString(element, "value", 4_096, out var value))
        {
            return false;
        }

        signature = new SkillSignature(algorithm!, keyId!, value!);
        return true;
    }

    private static bool TryStringArray(
        JsonElement root,
        string property,
        int maximumCount,
        int maximumLength,
        out IReadOnlyList<string>? values)
    {
        values = null;
        if (!root.TryGetProperty(property, out var element) || element.ValueKind is not JsonValueKind.Array ||
            element.GetArrayLength() > maximumCount)
        {
            return false;
        }

        var result = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.String || item.GetString() is not { } value ||
                string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            {
                return false;
            }

            result.Add(value);
        }

        values = result;
        return result.Distinct(StringComparer.Ordinal).Count() == result.Count;
    }

    private static bool TryString(JsonElement root, string property, int maximumLength, out string? value)
    {
        value = null;
        return root.TryGetProperty(property, out var element) && element.ValueKind is JsonValueKind.String &&
            (value = element.GetString()) is { } text && !string.IsNullOrWhiteSpace(text) &&
            text.Length <= maximumLength && text.All(character => !char.IsControl(character));
    }

    private static bool HasExactProperties(JsonElement element, params string[] allowed)
    {
        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        return names.All(name => allowed.Contains(name, StringComparer.Ordinal)) &&
            allowed.All(name => names.Contains(name, StringComparer.Ordinal));
    }

    private static bool HasDuplicateJsonProperties(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 32 });
        var stack = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType is JsonTokenType.StartObject)
            {
                stack.Push(new HashSet<string>(StringComparer.Ordinal));
            }
            else if (reader.TokenType is JsonTokenType.EndObject)
            {
                stack.Pop();
            }
            else if (reader.TokenType is JsonTokenType.PropertyName &&
                !stack.Peek().Add(reader.GetString()!))
            {
                return true;
            }
        }

        return false;
    }

    private static ReadOnlyMemory<byte> CreateCanonicalBundle(IReadOnlyDictionary<string, byte[]> files)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var file in files)
        {
            var path = Encoding.UTF8.GetBytes(file.Key);
            BinaryPrimitives.WriteInt32BigEndian(length, path.Length);
            stream.Write(length);
            stream.Write(path);
            BinaryPrimitives.WriteInt32BigEndian(length, file.Value.Length);
            stream.Write(length);
            stream.Write(file.Value);
        }

        return stream.ToArray();
    }

    private static byte[] CreateUnsignedManifest(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (string.Equals(property.Name, "signature", StringComparison.Ordinal))
                {
                    writer.WriteNullValue();
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool HasLinkedParent(string root, string path)
    {
        var current = Directory.GetParent(path);
        while (current is not null && !string.Equals(current.FullName, root, StringComparison.Ordinal))
        {
            if (IsReparsePoint(current.FullName))
            {
                return true;
            }

            current = current.Parent;
        }

        return current is null;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    private static DomainResult<LoadedSkillPackage> Failure(string message) =>
        DomainResult.Fail<LoadedSkillPackage>(new DomainFailure(FailureCode.ValidationFailure, message));
}
