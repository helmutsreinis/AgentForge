using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Persistence;
using AgentForge.Domain.Primitives;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AgentForge.Persistence;

internal sealed class DatabaseBackupService(
    AgentForgeDbContext dbContext,
    IDataDirectoryProvider dataDirectoryProvider,
    IOptions<PersistenceOptions> options,
    IClock clock) : IDatabaseBackupService
{
    private const string ManifestFileName = "backup.manifest.json";

    public async Task<DomainResult<DatabaseBackupManifest>> CreateAsync(
        CreateDatabaseBackupRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryPrepareEmptyDirectory(request.DestinationDirectory, out var destination, out var failure))
            return DomainResult.Fail<DatabaseBackupManifest>(failure!);
        var source = Path.GetFullPath(dataDirectoryProvider.GetDataDirectory());
        if (Overlaps(source, destination!)) return InvalidManifest("Backup destination must be outside the data directory.");
        try
        {
            var evidence = new List<BackupFileEvidence>();
            var databaseDirectory = Path.Combine(destination!, "database");
            Directory.CreateDirectory(databaseDirectory);
            var provider = options.Value.Provider == PersistenceProvider.PostgreSql
                ? DatabaseBackupProvider.PostgreSql
                : DatabaseBackupProvider.Sqlite;
            if (provider == DatabaseBackupProvider.Sqlite)
            {
                var databasePath = Path.Combine(databaseDirectory, "agentforge.db");
                var backedUp = await BackupSqliteAsync(databasePath, cancellationToken);
                if (!backedUp.IsSuccess) return DomainResult.Fail<DatabaseBackupManifest>(backedUp.Failure!);
                evidence.Add(await EvidenceAsync(destination!, databasePath, cancellationToken));
            }
            else
            {
                var dumpPath = Path.Combine(databaseDirectory, "agentforge.dump");
                var dumped = await DumpPostgreSqlAsync(dumpPath, cancellationToken);
                if (!dumped.IsSuccess) return DomainResult.Fail<DatabaseBackupManifest>(dumped.Failure!);
                evidence.Add(await EvidenceAsync(destination!, dumpPath, cancellationToken));
            }
            var artifacts = await CopyArtifactsAsync(source, destination!, evidence, cancellationToken);
            if (!artifacts.IsSuccess) return DomainResult.Fail<DatabaseBackupManifest>(artifacts.Failure!);
            evidence.Sort((left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
            var manifest = new DatabaseBackupManifest(
                1, Guid.NewGuid(), provider, clock.UtcNow, evidence,
                "sha256:0000000000000000000000000000000000000000000000000000000000000000");
            manifest = manifest with { ManifestHash = DatabaseBackupManifestValidator.ComputeHash(manifest) };
            await File.WriteAllBytesAsync(Path.Combine(destination!, ManifestFileName),
                JsonSerializer.SerializeToUtf8Bytes(manifest), cancellationToken);
            return DomainResult.Success(manifest);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException or NpgsqlException)
        {
            return DomainResult.Fail<DatabaseBackupManifest>(new DomainFailure(
                FailureCode.RecoverableExternalFailure, "Database backup could not be completed.", true));
        }
    }

    public async Task<DomainResult<bool>> VerifyAsync(
        string backupDirectory,
        DatabaseBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        var validation = DatabaseBackupManifestValidator.Validate(manifest);
        if (!validation.IsSuccess) return validation;
        if (!TryNormalizeExistingDirectory(backupDirectory, out var root))
            return Invalid("Backup directory is unavailable.");
        foreach (var item in manifest.Files)
        {
            var path = ContainedPath(root!, item.RelativePath);
            if (path is null || !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return Invalid("A backup file is missing or outside the package.");
            var info = new FileInfo(path);
            if (info.Length != item.Length || !string.Equals(
                    await HashFileAsync(path, cancellationToken), item.ContentHash, StringComparison.Ordinal))
                return Invalid("A backup file failed length or hash verification.");
        }
        return DomainResult.Success(true);
    }

    public async Task<DomainResult<bool>> RestoreAsync(
        RestoreDatabaseBackupRequest request,
        CancellationToken cancellationToken)
    {
        var verified = await VerifyAsync(request.BackupDirectory, request.Manifest, cancellationToken);
        if (!verified.IsSuccess) return verified;
        if (!TryPrepareEmptyDirectory(request.TargetDataDirectory, out var target, out var failure))
            return DomainResult.Fail<bool>(failure!);
        var current = Path.GetFullPath(dataDirectoryProvider.GetDataDirectory());
        var backup = Path.GetFullPath(request.BackupDirectory);
        if (Overlaps(target!, current) || Overlaps(target!, backup))
            return Invalid("Restore target must be a separate empty directory.");
        try
        {
            if (request.Manifest.Provider == DatabaseBackupProvider.PostgreSql)
            {
                var restored = await RestorePostgreSqlAsync(
                    Path.Combine(backup, "database", "agentforge.dump"),
                    request.PostgreSqlTargetConnectionStringEnvironmentVariable,
                    cancellationToken);
                if (!restored.IsSuccess) return restored;
            }
            else
            {
                var databaseSource = Path.Combine(backup, "database", "agentforge.db");
                await CopyFileAsync(databaseSource, Path.Combine(target!, options.Value.DatabaseFileName), cancellationToken);
            }
            foreach (var item in request.Manifest.Files.Where(item =>
                         item.RelativePath.StartsWith("artifacts/", StringComparison.Ordinal)))
            {
                var source = ContainedPath(backup, item.RelativePath)!;
                var relative = item.RelativePath["artifacts/".Length..];
                var destination = Path.Combine(target!, options.Value.ArtifactDirectoryName,
                    relative.Replace('/', Path.DirectorySeparatorChar));
                await CopyFileAsync(source, destination, cancellationToken);
            }
            return DomainResult.Success(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException or NpgsqlException)
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.RecoverableExternalFailure, "Database restore could not be completed.", true));
        }
    }

    private async Task<DomainResult<bool>> BackupSqliteAsync(string path, CancellationToken cancellationToken)
    {
        if (dbContext.Database.GetDbConnection() is not SqliteConnection source)
            return Invalid("SQLite backup requires the SQLite persistence provider.");
        await source.OpenAsync(cancellationToken);
        try
        {
            await using var destination = new SqliteConnection($"Data Source={path};Pooling=False");
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);
            return DomainResult.Success(true);
        }
        finally
        {
            await source.CloseAsync();
        }
    }

    private async Task<DomainResult<bool>> DumpPostgreSqlAsync(
        string path, CancellationToken cancellationToken)
    {
        var raw = System.Environment.GetEnvironmentVariable(
            options.Value.PostgreSqlConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw)) return Invalid("PostgreSQL connection secret is unavailable.");
        var connection = new NpgsqlConnectionStringBuilder(raw);
        var password = connection.Password;
        connection.Remove("Password");
        return await RunPostgreSqlToolAsync(
            options.Value.PostgreSqlDumpExecutable,
            ["--format=custom", "--no-owner", "--no-privileges", "--file", path, "--dbname", connection.ConnectionString],
            password ?? string.Empty,
            cancellationToken);
    }

    private async Task<DomainResult<bool>> RestorePostgreSqlAsync(
        string path,
        string? targetVariable,
        CancellationToken cancellationToken)
    {
        if (!IsEnvironmentVariableName(targetVariable) || string.Equals(
                targetVariable, options.Value.PostgreSqlConnectionStringEnvironmentVariable, StringComparison.Ordinal))
            return Invalid("PostgreSQL restore requires a distinct explicit target connection environment variable.");
        var raw = System.Environment.GetEnvironmentVariable(targetVariable!);
        if (string.IsNullOrWhiteSpace(raw)) return Invalid("PostgreSQL target connection secret is unavailable.");
        var connection = new NpgsqlConnectionStringBuilder(raw);
        var password = connection.Password;
        connection.Remove("Password");
        return await RunPostgreSqlToolAsync(
            options.Value.PostgreSqlRestoreExecutable,
            ["--clean", "--if-exists", "--no-owner", "--no-privileges", "--dbname",
                connection.ConnectionString, path],
            password ?? string.Empty,
            cancellationToken);
    }

    private static async Task<DomainResult<bool>> RunPostgreSqlToolAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            return Invalid("An exact PostgreSQL backup tool path is not configured.");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.Environment.Clear();
        if (!string.IsNullOrEmpty(password)) process.StartInfo.Environment["PGPASSWORD"] = password;
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) return Invalid("PostgreSQL backup tool could not start.");
        process.StartInfo.Environment.Clear();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, 65_536, timeout.Token);
            var stderr = ReadBoundedAsync(process.StandardError.BaseStream, 65_536, timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr);
            return process.ExitCode == 0
                ? DomainResult.Success(true)
                : DomainResult.Fail<bool>(new DomainFailure(
                    FailureCode.RecoverableExternalFailure, "PostgreSQL backup tool returned a failure.", true));
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidOperationException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between observation and cleanup.
            }
            return DomainResult.Fail<bool>(new DomainFailure(
                cancellationToken.IsCancellationRequested ? FailureCode.Cancelled : FailureCode.BudgetExceeded,
                "PostgreSQL backup tool exceeded its output/time bound or was cancelled.", true));
        }
    }

    private async Task<DomainResult<bool>> CopyArtifactsAsync(
        string sourceRoot,
        string destinationRoot,
        List<BackupFileEvidence> evidence,
        CancellationToken cancellationToken)
    {
        var source = Path.Combine(sourceRoot, options.Value.ArtifactDirectoryName);
        if (!Directory.Exists(source)) return DomainResult.Success(true);
        if (Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).Any(directory =>
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0))
            return Invalid("Artifact backup refuses filesystem links.");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                return Invalid("Artifact backup refuses filesystem links.");
            var relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith("..", StringComparison.Ordinal)) return Invalid("Artifact path escaped its root.");
            var destination = Path.Combine(destinationRoot, "artifacts", relative);
            await CopyFileAsync(file, destination, cancellationToken);
            evidence.Add(await EvidenceAsync(destinationRoot, destination, cancellationToken));
        }
        return DomainResult.Success(true);
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65_536, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static async Task<BackupFileEvidence> EvidenceAsync(
        string root, string path, CancellationToken cancellationToken) => new(
            Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
            new FileInfo(path).Length,
            await HashFileAsync(path, cancellationToken));

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken);
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) return memory.ToArray();
            if (memory.Length + count > maximumBytes) throw new IOException("Process output exceeded bound.");
            await memory.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }

    private static bool TryPrepareEmptyDirectory(
        string value, out string? normalized, out DomainFailure? failure)
    {
        normalized = null;
        failure = null;
        try
        {
            normalized = Path.GetFullPath(value);
            if (File.Exists(normalized) || Directory.Exists(normalized) &&
                Directory.EnumerateFileSystemEntries(normalized).Any())
            {
                failure = new DomainFailure(FailureCode.ValidationFailure,
                    "Target directory must be absent or empty.");
                return false;
            }
            Directory.CreateDirectory(normalized);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or
            NotSupportedException or PathTooLongException)
        {
            failure = new DomainFailure(FailureCode.ValidationFailure, "Target directory is invalid or unavailable.");
            return false;
        }
    }

    private static bool TryNormalizeExistingDirectory(string value, out string? normalized)
    {
        normalized = null;
        try
        {
            normalized = Path.GetFullPath(value);
            return Directory.Exists(normalized) &&
                (File.GetAttributes(normalized) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or
            NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? ContainedPath(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static bool Overlaps(string left, string right)
    {
        left = left.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        right = right.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return left.StartsWith(right, StringComparison.OrdinalIgnoreCase) ||
            right.StartsWith(left, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEnvironmentVariableName(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 && (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static DomainResult<DatabaseBackupManifest> InvalidManifest(string message) =>
        DomainResult.Fail<DatabaseBackupManifest>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<bool> Invalid(string message) =>
        DomainResult.Fail<bool>(new DomainFailure(FailureCode.ValidationFailure, message));
}
