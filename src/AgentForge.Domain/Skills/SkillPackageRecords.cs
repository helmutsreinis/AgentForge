using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Skills;

public readonly record struct SkillId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct SkillVersion(string Value) : IComparable<SkillVersion>
{
    public static bool operator <(SkillVersion left, SkillVersion right) => left.CompareTo(right) < 0;

    public static bool operator <=(SkillVersion left, SkillVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >(SkillVersion left, SkillVersion right) => left.CompareTo(right) > 0;

    public static bool operator >=(SkillVersion left, SkillVersion right) => left.CompareTo(right) >= 0;

    public static bool TryParse(string? value, out SkillVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.StartsWith('v') ||
            value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return false;
        }

        var buildSplit = value.Split('+', 2);
        var preSplit = buildSplit[0].Split('-', 2);
        var core = preSplit[0].Split('.');
        if (core.Length != 3 || core.Any(component => !TryNumeric(component, out _)) ||
            preSplit.Length == 2 && !TryIdentifiers(preSplit[1], numericNoLeadingZero: true) ||
            buildSplit.Length == 2 && !TryIdentifiers(buildSplit[1], numericNoLeadingZero: false))
        {
            return false;
        }

        version = new SkillVersion(value);
        return true;
    }

    public int CompareTo(SkillVersion other)
    {
        Parse(Value, out var leftCore, out var leftPre);
        Parse(other.Value, out var rightCore, out var rightPre);
        for (var index = 0; index < 3; index++)
        {
            var comparison = leftCore[index].CompareTo(rightCore[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        if (leftPre.Length == 0 || rightPre.Length == 0)
        {
            return leftPre.Length == rightPre.Length ? 0 : leftPre.Length == 0 ? 1 : -1;
        }

        for (var index = 0; index < Math.Max(leftPre.Length, rightPre.Length); index++)
        {
            if (index >= leftPre.Length || index >= rightPre.Length)
            {
                return leftPre.Length.CompareTo(rightPre.Length);
            }

            var leftNumeric = long.TryParse(leftPre[index], NumberStyles.None, CultureInfo.InvariantCulture, out var left);
            var rightNumeric = long.TryParse(rightPre[index], NumberStyles.None, CultureInfo.InvariantCulture, out var right);
            var comparison = leftNumeric && rightNumeric
                ? left.CompareTo(right)
                : leftNumeric != rightNumeric
                    ? leftNumeric ? -1 : 1
                    : string.CompareOrdinal(leftPre[index], rightPre[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    public override string ToString() => Value;

    private static void Parse(string value, out long[] core, out string[] prerelease)
    {
        var withoutBuild = value.Split('+', 2)[0];
        var parts = withoutBuild.Split('-', 2);
        core = parts[0].Split('.').Select(component => long.Parse(component, CultureInfo.InvariantCulture)).ToArray();
        prerelease = parts.Length == 1 ? [] : parts[1].Split('.');
    }

    private static bool TryNumeric(string value, out long parsed)
    {
        parsed = 0;
        return !string.IsNullOrEmpty(value) && (value.Length == 1 || value[0] != '0') &&
            long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed >= 0;
    }

    private static bool TryIdentifiers(string value, bool numericNoLeadingZero) =>
        value.Split('.').All(identifier => !string.IsNullOrEmpty(identifier) &&
            identifier.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') &&
            (!numericNoLeadingZero || !identifier.All(char.IsAsciiDigit) ||
                identifier.Length == 1 || identifier[0] != '0'));
}

public sealed record SkillDependency(SkillId Id, SkillVersion Version);

public sealed record SkillRequirements(
    IReadOnlyList<string> OperatingSystems,
    IReadOnlyList<string> ModelCapabilities,
    IReadOnlyList<string> ToolIds);

public sealed record SkillSignature(string Algorithm, string KeyId, string Value);

public sealed record SkillPackage(
    SkillId Id,
    SkillVersion Version,
    string Description,
    string Markdown,
    IReadOnlyList<SkillDependency> Dependencies,
    SkillRequirements Requirements,
    IReadOnlyList<string> Permissions,
    IReadOnlyDictionary<string, string> FileHashes,
    string ManifestHash,
    string PackageHash,
    SkillSignature? Signature);

public sealed record LoadedSkillPackage(SkillPackage Package, ReadOnlyMemory<byte> CanonicalBytes);

public static class SkillPackageValidator
{
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    public static DomainResult<bool> Validate(SkillPackage? package)
    {
        if (package is null || !IsSkillId(package.Id) || !SkillVersion.TryParse(package.Version.Value, out _) ||
            !IsBounded(package.Description, 2_048) || string.IsNullOrWhiteSpace(package.Markdown) ||
            package.Markdown.Length > 262_144 || package.Dependencies is null || package.Dependencies.Count > 128 ||
            package.Dependencies.Any(dependency => !IsSkillId(dependency.Id) ||
                !SkillVersion.TryParse(dependency.Version.Value, out _) || dependency.Id == package.Id) ||
            package.Dependencies.Select(dependency => dependency.Id).Distinct().Count() != package.Dependencies.Count ||
            package.Requirements is null ||
            !IsDistinctBounded(package.Requirements.OperatingSystems, 16, 64) ||
            package.Requirements.OperatingSystems.Any(value => value is not ("windows" or "linux" or "macos")) ||
            !IsDistinctBounded(package.Requirements.ModelCapabilities, 128, 256) ||
            !IsDistinctBounded(package.Requirements.ToolIds, 128, 256) ||
            package.Requirements.ToolIds.Any(value => !value.StartsWith("tool:", StringComparison.Ordinal)) ||
            !IsDistinctBounded(package.Permissions, 128, 256) ||
            package.FileHashes is null || package.FileHashes.Count is < 2 or > 128 ||
            package.FileHashes.Any(item => !IsRelativePackagePath(item.Key) || !IsHash(item.Value)) ||
            !package.FileHashes.ContainsKey("SKILL.md") || !package.FileHashes.ContainsKey("skill.harness.json") ||
            !IsHash(package.ManifestHash) || !IsHash(package.PackageHash) ||
            package.Signature is { } signature && (!IsBounded(signature.Algorithm, 64) ||
                !IsBounded(signature.KeyId, 256) || !IsBounded(signature.Value, 4_096)))
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Skill package metadata, contents, requirements, permissions, or hashes are invalid."));
        }

        return DomainResult.Success(true);
    }

    public static DomainResult<bool> ValidateDependencyGraph(IReadOnlyList<SkillPackage> packages)
    {
        if (packages is null || packages.Count > 4_096 || packages.Any(package => !Validate(package).IsSuccess) ||
            packages.Select(package => (package.Id, package.Version)).Distinct().Count() != packages.Count)
        {
            return Failure("The skill dependency catalog is invalid or contains duplicate versions.");
        }

        var exact = packages.ToDictionary(package => (package.Id, package.Version));
        if (packages.Any(package => package.Dependencies.Any(dependency => !exact.ContainsKey((dependency.Id, dependency.Version)))))
        {
            return Failure("A skill dependency references a missing exact package version.");
        }

        var visiting = new HashSet<(SkillId, SkillVersion)>();
        var visited = new HashSet<(SkillId, SkillVersion)>();
        bool Visit((SkillId, SkillVersion) key)
        {
            if (visiting.Contains(key))
            {
                return true;
            }

            if (!visited.Add(key))
            {
                return false;
            }

            visiting.Add(key);
            var cycle = exact[key].Dependencies.Any(dependency => Visit((dependency.Id, dependency.Version)));
            visiting.Remove(key);
            return cycle;
        }

        return exact.Keys.Any(Visit) ? Failure("The skill dependency graph contains a cycle.") : DomainResult.Success(true);
    }

    public static bool IsHash(string? value) => value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept(LowerHex) < 0;

    private static bool IsSkillId(SkillId id) => IsBounded(id.Value, 256) &&
        id.Value.StartsWith("skill:", StringComparison.Ordinal) &&
        id.Value.AsSpan(6).IndexOfAnyExcept(SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789._-")) < 0;

    private static bool IsDistinctBounded(
        IReadOnlyList<string>? values,
        int maximumCount,
        int maximumLength) => values is not null && values.Count <= maximumCount &&
        values.All(value => IsBounded(value, maximumLength)) &&
        values.Distinct(StringComparer.Ordinal).Count() == values.Count;

    private static bool IsRelativePackagePath(string path) => IsBounded(path, 512) &&
        !Path.IsPathRooted(path) && !path.Contains('\\') &&
        path.Split('/').All(part => part is not ("" or "." or ".."));

    private static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => !char.IsControl(character));

    private static DomainResult<bool> Failure(string message) =>
        DomainResult.Fail<bool>(new DomainFailure(FailureCode.ValidationFailure, message));
}
