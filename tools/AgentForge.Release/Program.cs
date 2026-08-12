using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentForge.Release;

internal static partial class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length < 1) return Usage();
            var options = ParseOptions(args[1..]);
            if (args[0] == "manifest")
            {
                ReleaseManifestGenerator.Generate(
                    Required(options, "--release-directory"),
                    Required(options, "--repository-root"),
                    Required(options, "--version"),
                    Required(options, "--commit"),
                    DateTimeOffset.Parse(Required(options, "--created"),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind));
                Console.WriteLine("{\"status\":\"generated\"}");
                return 0;
            }
            if (args[0] == "verify")
            {
                var errors = ReleaseManifestGenerator.Verify(Required(options, "--release-directory"));
                Console.WriteLine(JsonSerializer.Serialize(new { status = errors.Count == 0 ? "valid" : "invalid", errors }));
                return errors.Count == 0 ? 0 : 1;
            }
            if (args[0] == "archive")
            {
                ReleasePackageBuilder.CreateArchive(
                    Required(options, "--source-directory"),
                    Required(options, "--output-path"),
                    Required(options, "--format"),
                    DateTimeOffset.Parse(Required(options, "--created"),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind));
                Console.WriteLine("{\"status\":\"archived\"}");
                return 0;
            }
            if (args[0] == "smoke")
                return ReleasePackageBuilder.SmokeAsync(
                    Required(options, "--release-directory"),
                    Required(options, "--rid"),
                    CancellationToken.None).GetAwaiter().GetResult();
            return Usage();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or
            UnauthorizedAccessException or JsonException or FormatException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = exception.Message }));
            return 1;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        if (args.Length % 2 != 0) throw new ArgumentException("Release options must be exact name/value pairs.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) ||
                !result.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException("Release option names must be unique and start with '--'.");
        }
        return result;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required option {name}.");

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: agentforge-release manifest|verify --release-directory <path> [manifest options]");
        return 1;
    }
}

public static class ReleasePackageBuilder
{
    public static void CreateArchive(
        string sourceDirectory,
        string outputPath,
        string format,
        DateTimeOffset created)
    {
        var source = ExistingDirectory(sourceDirectory);
        var output = Path.GetFullPath(outputPath);
        if (output.StartsWith(source + Path.DirectorySeparatorChar, PathComparison()))
            throw new ArgumentException("Archive output must be outside its source directory.");
        var parent = Path.GetDirectoryName(output);
        if (parent is null || !Directory.Exists(parent))
            throw new ArgumentException("Archive output parent must exist.");
        EnsureLinkFree(source);
        if (File.Exists(output)) File.Delete(output);
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(path => new ArchiveFile(path, Path.GetRelativePath(source, path).Replace('\\', '/')))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0) throw new InvalidDataException("Archive source is empty.");
        var timestamp = created.ToUniversalTime();
        if (format == "zip") WriteZip(output, files, timestamp);
        else if (format == "tar.gz") WriteTarGzip(output, files, timestamp);
        else throw new ArgumentException("Archive format must be zip or tar.gz.");
    }

    public static async Task<int> SmokeAsync(
        string releaseDirectory,
        string runtimeIdentifier,
        CancellationToken cancellationToken)
    {
        if (runtimeIdentifier is not ("win-x64" or "linux-x64"))
            throw new ArgumentException("Smoke runtime identifier is unsupported.");
        if (runtimeIdentifier.StartsWith("win", StringComparison.Ordinal) != OperatingSystem.IsWindows())
            throw new ArgumentException("Smoke package must match the current operating system.");
        var releaseRoot = ExistingDirectory(releaseDirectory);
        var packageRoot = Path.Combine(releaseRoot, runtimeIdentifier);
        var hostPath = Path.Combine(packageRoot, "host",
            OperatingSystem.IsWindows() ? "AgentForge.Host.exe" : "AgentForge.Host");
        var cliPath = Path.Combine(packageRoot, "cli", OperatingSystem.IsWindows() ? "agentforge.exe" : "agentforge");
        if (!File.Exists(hostPath) || !File.Exists(cliPath))
            throw new InvalidDataException("Self-contained host or CLI executable is missing.");
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"agentforge-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var endpoint = $"http://127.0.0.1:{port}";
        using var host = Start(hostPath,
            [$"--AgentForge:Installation:DataDirectory={dataDirectory}", $"--AgentForge:Host:Urls={endpoint}"],
            new Dictionary<string, string> { ["DOTNET_ENVIRONMENT"] = "Production" });
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(2) };
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (host.HasExited) throw new InvalidDataException($"Packaged host exited with code {host.ExitCode}.");
                try
                {
                    using var response = await client.GetAsync("/health/live", timeout.Token);
                    if (response.StatusCode == HttpStatusCode.OK) break;
                }
                catch (HttpRequestException)
                {
                    // The listener is not ready yet.
                }
                catch (TaskCanceledException) when (!timeout.IsCancellationRequested)
                {
                    // The short per-request timeout elapsed while the host was starting.
                }
                await Task.Delay(200, timeout.Token);
            }
            using var cli = Start(cliPath, ["status"], new Dictionary<string, string>
            {
                ["AGENTFORGE_ENDPOINT"] = endpoint,
            });
            await cli.WaitForExitAsync(timeout.Token);
            if (cli.ExitCode is not (0 or 2))
                throw new InvalidDataException($"Packaged CLI exited with code {cli.ExitCode}.");
            Console.WriteLine(JsonSerializer.Serialize(new { status = "pass", runtimeIdentifier }));
            return 0;
        }
        finally
        {
            if (!host.HasExited) host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync(CancellationToken.None);
            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // A native SQLite handle can finish releasing just after process exit.
            }
        }
    }

    private static Process Start(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidDataException("Packaged process did not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static void WriteZip(string output, IReadOnlyList<ArchiveFile> files, DateTimeOffset timestamp)
    {
        var zipTimestamp = timestamp < new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            : timestamp;
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);
        foreach (var file in files)
        {
            var entry = archive.CreateEntry(file.RelativePath, CompressionLevel.SmallestSize);
            entry.LastWriteTime = zipTimestamp;
            entry.ExternalAttributes = (IsExecutable(file.RelativePath) ? Convert.ToInt32("100755", 8) :
                Convert.ToInt32("100644", 8)) << 16;
            using var source = File.OpenRead(file.FullPath);
            using var target = entry.Open();
            source.CopyTo(target);
        }
    }

    private static void WriteTarGzip(string output, IReadOnlyList<ArchiveFile> files, DateTimeOffset timestamp)
    {
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var gzip = new GZipStream(stream, CompressionLevel.SmallestSize, leaveOpen: false);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);
        foreach (var file in files)
        {
            using var source = File.OpenRead(file.FullPath);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, file.RelativePath)
            {
                DataStream = source,
                ModificationTime = timestamp,
                Gid = 0,
                Uid = 0,
                GroupName = "root",
                UserName = "root",
                Mode = IsExecutable(file.RelativePath)
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                      UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                      UnixFileMode.OtherExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            };
            writer.WriteEntry(entry);
        }
    }

    private static bool IsExecutable(string path) => !path.Contains('.', StringComparison.Ordinal) ||
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".sh", StringComparison.OrdinalIgnoreCase);

    private static void EnsureLinkFree(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Archive source cannot contain links.");
    }

    private static string ExistingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Directory path is required.");
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) throw new ArgumentException($"Directory does not exist: {fullPath}");
        return fullPath;
    }

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record ArchiveFile(string FullPath, string RelativePath);
}

public static partial class ReleaseManifestGenerator
{
    private const string ChecksumName = "SHA256SUMS";
    private const string SbomName = "AgentForge.spdx.json";
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    public static void Generate(
        string releaseDirectory,
        string repositoryRoot,
        string version,
        string commit,
        DateTimeOffset created)
    {
        var releaseRoot = ExistingDirectory(releaseDirectory);
        var sourceRoot = ExistingDirectory(repositoryRoot);
        if (!VersionRegex().IsMatch(version)) throw new ArgumentException("Version must be stable SemVer.");
        if (!CommitRegex().IsMatch(commit)) throw new ArgumentException("Commit must be a hexadecimal Git object ID.");
        EnsureLinkFree(releaseRoot, includeFiles: true);

        var componentFiles = EnumerateFiles(releaseRoot)
            .Where(file => !IsGenerated(file.RelativePath))
            .ToArray();
        if (componentFiles.Length == 0) throw new InvalidDataException("Release directory contains no package files.");
        var dependencies = ReadDependencies(sourceRoot);
        WriteJsonAtomically(Path.Combine(releaseRoot, SbomName), CreateSpdx(
            version, commit.ToLowerInvariant(), created.ToUniversalTime(), componentFiles, dependencies));

        var checksumFiles = EnumerateFiles(releaseRoot)
            .Where(file => !string.Equals(file.RelativePath, ChecksumName, StringComparison.Ordinal))
            .ToArray();
        var checksumText = string.Join('\n', checksumFiles.Select(file => $"{file.Sha256}  {file.RelativePath}")) + "\n";
        WriteTextAtomically(Path.Combine(releaseRoot, ChecksumName), checksumText);
    }

    public static IReadOnlyList<string> Verify(string releaseDirectory)
    {
        var errors = new List<string>();
        string releaseRoot;
        try
        {
            releaseRoot = ExistingDirectory(releaseDirectory);
            EnsureLinkFree(releaseRoot, includeFiles: true);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return [exception.Message];
        }

        var checksumPath = Path.Combine(releaseRoot, ChecksumName);
        if (!File.Exists(checksumPath)) return ["SHA256SUMS is missing."];
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(checksumPath, Encoding.UTF8))
        {
            if (line.Length == 0) continue;
            var separator = line.IndexOf("  ", StringComparison.Ordinal);
            if (separator != 64 || !HashRegex().IsMatch(line[..separator]))
            {
                errors.Add("SHA256SUMS contains an invalid line.");
                continue;
            }
            var relative = line[(separator + 2)..].Replace('\\', '/');
            if (!IsSafeRelativePath(relative) || !declared.TryAdd(relative, line[..separator]))
                errors.Add("SHA256SUMS contains an unsafe or duplicate path.");
        }

        var actual = EnumerateFiles(releaseRoot)
            .Where(file => !string.Equals(file.RelativePath, ChecksumName, StringComparison.Ordinal))
            .ToDictionary(file => file.RelativePath, file => file.Sha256, StringComparer.Ordinal);
        foreach (var path in actual.Keys.Except(declared.Keys, StringComparer.Ordinal))
            errors.Add($"Checksum is missing for {path}.");
        foreach (var path in declared.Keys.Except(actual.Keys, StringComparer.Ordinal))
            errors.Add($"Checksum names a missing file: {path}.");
        foreach (var pair in actual.Where(pair => declared.TryGetValue(pair.Key, out var hash) &&
                     !CryptographicOperations.FixedTimeEquals(
                         Convert.FromHexString(pair.Value), Convert.FromHexString(hash))))
            errors.Add($"Checksum mismatch for {pair.Key}.");

        var sbomPath = Path.Combine(releaseRoot, SbomName);
        if (!File.Exists(sbomPath)) errors.Add("SPDX SBOM is missing.");
        else
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(sbomPath), new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                });
                var root = document.RootElement;
                if (root.GetProperty("spdxVersion").GetString() != "SPDX-2.3" ||
                    root.GetProperty("packages").GetArrayLength() < 1 ||
                    root.GetProperty("files").GetArrayLength() < 1)
                    errors.Add("SPDX SBOM is incomplete.");
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                errors.Add($"SPDX SBOM is invalid: {exception.Message}");
            }
        }
        return errors;
    }

    private static object CreateSpdx(
        string version,
        string commit,
        DateTimeOffset created,
        IReadOnlyList<ReleaseFile> files,
        IReadOnlyList<Dependency> dependencies)
    {
        var spdxFiles = files.Select((file, index) => new
        {
            fileName = $"./{file.RelativePath}",
            SPDXID = $"SPDXRef-File-{index + 1}",
            checksums = new[] { new { algorithm = "SHA256", checksumValue = file.Sha256 } },
            fileTypes = new[] { Classify(file.RelativePath) },
        }).ToArray();
        var dependencyPackages = dependencies.Select((dependency, index) => new
        {
            name = dependency.Name,
            SPDXID = $"SPDXRef-NuGet-{index + 1}",
            versionInfo = dependency.Version,
            downloadLocation = $"https://www.nuget.org/packages/{Uri.EscapeDataString(dependency.Name)}/{Uri.EscapeDataString(dependency.Version)}",
            filesAnalyzed = false,
            licenseConcluded = "NOASSERTION",
            licenseDeclared = "NOASSERTION",
            copyrightText = "NOASSERTION",
            externalRefs = new[]
            {
                new
                {
                    referenceCategory = "PACKAGE-MANAGER",
                    referenceType = "purl",
                    referenceLocator = $"pkg:nuget/{Uri.EscapeDataString(dependency.Name)}@{Uri.EscapeDataString(dependency.Version)}",
                },
            },
        }).ToArray<object>();
        var packages = new List<object>
        {
            new
            {
                name = "AgentForge",
                SPDXID = "SPDXRef-Package-AgentForge",
                versionInfo = version,
                downloadLocation = "NOASSERTION",
                filesAnalyzed = false,
                licenseConcluded = "NOASSERTION",
                licenseDeclared = "NOASSERTION",
                copyrightText = "Copyright Helmuts Reinis",
                externalRefs = new[]
                {
                    new
                    {
                        referenceCategory = "PACKAGE-MANAGER",
                        referenceType = "purl",
                        referenceLocator = $"pkg:github/helmutsreinis/AgentForge@{commit}",
                    },
                },
            },
        };
        packages.AddRange(dependencyPackages);
        var relationships = spdxFiles.Select(file => new
        {
            spdxElementId = "SPDXRef-Package-AgentForge",
            relationshipType = "CONTAINS",
            relatedSpdxElement = file.SPDXID,
        }).Cast<object>().Concat(dependencyPackages.Select(package => new
        {
            spdxElementId = "SPDXRef-Package-AgentForge",
            relationshipType = "DEPENDS_ON",
            relatedSpdxElement = package.GetType().GetProperty("SPDXID")!.GetValue(package),
        })).ToArray();
        return new
        {
            spdxVersion = "SPDX-2.3",
            dataLicense = "CC0-1.0",
            SPDXID = "SPDXRef-DOCUMENT",
            name = $"AgentForge-{version}",
            documentNamespace = $"https://github.com/helmutsreinis/AgentForge/sbom/{Uri.EscapeDataString(version)}/{commit}",
            creationInfo = new
            {
                created = created.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                creators = new[] { "Tool: AgentForge.Release-1.0", "Person: Helmuts Reinis" },
            },
            documentDescribes = new[] { "SPDXRef-Package-AgentForge" },
            packages,
            files = spdxFiles,
            relationships,
        };
    }

    private static Dependency[] ReadDependencies(string repositoryRoot)
    {
        var results = new Dictionary<string, Dependency>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(repositoryRoot, "packages.lock.json", SearchOption.AllDirectories)
                     .Where(path => !IsBuildDirectory(path)))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!document.RootElement.TryGetProperty("dependencies", out var frameworks)) continue;
            foreach (var framework in frameworks.EnumerateObject())
            {
                foreach (var package in framework.Value.EnumerateObject())
                {
                    if (!package.Value.TryGetProperty("resolved", out var resolved) ||
                        string.IsNullOrWhiteSpace(resolved.GetString())) continue;
                    var dependency = new Dependency(package.Name, resolved.GetString()!);
                    results.TryAdd($"{dependency.Name}/{dependency.Version}", dependency);
                }
            }
        }
        return results.Values.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Version, StringComparer.Ordinal).ToArray();
    }

    private static bool IsBuildDirectory(string path) => path.Replace('\\', '/').Split('/')
        .Any(segment => segment is "bin" or "obj" or ".git" or "artifacts");

    private static ReleaseFile[] EnumerateFiles(string root) => Directory
        .EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => new ReleaseFile(
            Path.GetRelativePath(root, path).Replace('\\', '/'),
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))))
        .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
        .ToArray();

    private static void EnsureLinkFree(string root, bool includeFiles)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (includeFiles && File.Exists(path) && new FileInfo(path).LinkTarget is not null) ||
                (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is not null))
                throw new InvalidDataException("Release directory cannot contain links.");
        }
    }

    private static string ExistingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Directory path is required.");
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) throw new ArgumentException($"Directory does not exist: {fullPath}");
        return fullPath;
    }

    private static void WriteJsonAtomically(string path, object value)
    {
        WriteTextAtomically(path, JsonSerializer.Serialize(value, IndentedJsonOptions) + "\n");
    }

    private static void WriteTextAtomically(string path, string value)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, value, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool IsGenerated(string path) => path is ChecksumName or SbomName;

    private static bool IsSafeRelativePath(string path) => path.Length is > 0 and <= 4096 &&
        !Path.IsPathFullyQualified(path) && !path.Split('/').Any(segment => segment is "" or "." or "..") &&
        !path.Any(char.IsControl);

    private static string Classify(string path) =>
        path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "BINARY" : "OTHER";

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionRegex();

    [GeneratedRegex("^[0-9a-fA-F]{7,64}$")]
    private static partial Regex CommitRegex();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashRegex();

    private sealed record ReleaseFile(string RelativePath, string Sha256);
    private sealed record Dependency(string Name, string Version);
}
