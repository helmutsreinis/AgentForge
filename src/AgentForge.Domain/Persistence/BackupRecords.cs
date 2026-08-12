using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Persistence;

public enum DatabaseBackupProvider
{
    Sqlite,
    PostgreSql,
}

public sealed record BackupFileEvidence(string RelativePath, long Length, string ContentHash);

public sealed record DatabaseBackupManifest(
    int SchemaVersion,
    Guid BackupId,
    DatabaseBackupProvider Provider,
    DateTimeOffset CreatedAt,
    IReadOnlyList<BackupFileEvidence> Files,
    string ManifestHash);

public sealed record CreateDatabaseBackupRequest(string DestinationDirectory);

public sealed record RestoreDatabaseBackupRequest(
    string BackupDirectory,
    DatabaseBackupManifest Manifest,
    string TargetDataDirectory,
    string? PostgreSqlTargetConnectionStringEnvironmentVariable = null);

public static class DatabaseBackupManifestValidator
{
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    public static DomainResult<bool> Validate(DatabaseBackupManifest? value)
    {
        if (value is null || value.SchemaVersion != 1 || value.BackupId == Guid.Empty ||
            !Enum.IsDefined(value.Provider) || value.CreatedAt == default || value.Files is null ||
            value.Files.Count is < 1 or > 131_072 || value.Files.Any(file => !IsFile(file)) ||
            value.Files.Select(file => file.RelativePath).Distinct(StringComparer.Ordinal).Count() != value.Files.Count ||
            !IsHash(value.ManifestHash) || !string.Equals(value.ManifestHash, ComputeHash(value), StringComparison.Ordinal))
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.ValidationFailure, "Database backup manifest is invalid or has lost integrity."));
        return DomainResult.Success(true);
    }

    public static string ComputeHash(DatabaseBackupManifest manifest)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            manifest.SchemaVersion,
            manifest.BackupId,
            Provider = manifest.Provider.ToString(),
            CreatedAtUtcTicks = manifest.CreatedAt.UtcTicks,
            Files = manifest.Files.Select(file => new
            {
                file.RelativePath,
                file.Length,
                file.ContentHash,
            }),
        });
        return Hash(canonical);
    }

    public static string Hash(ReadOnlySpan<byte> bytes) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";

    private static bool IsFile(BackupFileEvidence file) => file.Length >= 0 &&
        !string.IsNullOrWhiteSpace(file.RelativePath) && file.RelativePath.Length <= 1024 &&
        !Path.IsPathRooted(file.RelativePath) && !file.RelativePath.Contains('\\') &&
        !file.RelativePath.Split('/').Any(part => part is "" or "." or "..") && IsHash(file.ContentHash);

    private static bool IsHash(string value) => value.Length == 71 &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept(LowerHex) < 0;
}
