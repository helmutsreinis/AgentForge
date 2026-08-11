using System.Security.Cryptography;
using System.Text;
using System.Xml;
using AgentForge.Abstractions.Coding;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Coding;
using AgentForge.Domain.Primitives;

namespace AgentForge.Coding;

internal sealed class RepositoryDiscovery(IClock clock) : IRepositoryDiscovery
{
    private const int MaximumFiles = 50_000;
    private const long MaximumMetadataFileBytes = 2_097_152;
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", ".nuget",
    };

    public async Task<DomainResult<RepositoryProfile>> DiscoverAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        if (!TryRoot(repositoryRoot, out var root))
        {
            return Failure("The repository root is missing, linked, or invalid.");
        }

        var enumerated = Enumerate(root!, cancellationToken);
        if (!enumerated.IsSuccess)
        {
            return DomainResult.Fail<RepositoryProfile>(enumerated.Failure!);
        }

        var files = enumerated.Value;
        var solutions = files.Where(path => Path.GetExtension(path) is ".sln" or ".slnx")
            .Order(StringComparer.Ordinal).ToArray();
        var projects = new List<RepositoryProject>();
        foreach (var path in files.Where(path => Path.GetExtension(path) is ".csproj" or ".fsproj" or ".vbproj"))
        {
            var parsed = await ParseProjectAsync(root!, path, cancellationToken);
            if (!parsed.IsSuccess)
            {
                return DomainResult.Fail<RepositoryProfile>(parsed.Failure!);
            }

            projects.Add(parsed.Value);
        }

        var instructions = new List<RepositoryInstruction>();
        foreach (var path in files.Where(IsInstruction))
        {
            var fullPath = Path.Combine(root!, FromSlash(path));
            var info = new FileInfo(fullPath);
            if (info.Length > MaximumMetadataFileBytes)
            {
                return Failure("A repository instruction exceeded the metadata byte bound.");
            }

            var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
            instructions.Add(new RepositoryInstruction(path, Hash(bytes), bytes.LongLength));
        }

        var languages = files.Select(LanguageFor).Where(value => value is not null).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).Cast<string>().ToArray();
        var buildSystems = DetectBuildSystems(files);
        var ci = files.Where(IsContinuousIntegration).Order(StringComparer.Ordinal).ToArray();
        var locks = files.Where(IsLockFile).Order(StringComparer.Ordinal).ToArray();
        var observedAt = clock.UtcNow;
        var profile = new RepositoryProfile(
            root!, string.Empty, solutions, projects.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray(),
            languages, buildSystems, ci, locks,
            instructions.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray(), observedAt);
        return DomainResult.Success(profile with { ProfileHash = ComputeHash(profile) });
    }

    private static DomainResult<IReadOnlyList<string>> Enumerate(string root, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        try
        {
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Pop();
                foreach (var directory in Directory.EnumerateDirectories(current).OrderDescending())
                {
                    var info = new DirectoryInfo(directory);
                    if (!IgnoredDirectories.Contains(info.Name) &&
                        (info.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(directory);
                    }
                }

                foreach (var file in Directory.EnumerateFiles(current).Order(StringComparer.Ordinal))
                {
                    var info = new FileInfo(file);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    files.Add(Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'));
                    if (files.Count > MaximumFiles)
                    {
                        return DomainResult.Fail<IReadOnlyList<string>>(new DomainFailure(
                            FailureCode.BudgetExceeded, "Repository discovery exceeded its file bound."));
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DomainResult.Fail<IReadOnlyList<string>>(new DomainFailure(
                FailureCode.RecoverableExternalFailure, "Repository metadata could not be read safely."));
        }

        return DomainResult.Success<IReadOnlyList<string>>(files.Order(StringComparer.Ordinal).ToArray());
    }

    private static async Task<DomainResult<RepositoryProject>> ParseProjectAsync(
        string root,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.Combine(root, FromSlash(relativePath));
        if (new FileInfo(fullPath).Length > MaximumMetadataFileBytes)
        {
            return DomainResult.Fail<RepositoryProject>(new DomainFailure(
                FailureCode.BudgetExceeded, "A project file exceeded the metadata byte bound."));
        }

        try
        {
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumMetadataFileBytes,
            });
            var document = new XmlDocument { XmlResolver = null };
            document.Load(reader);
            var rootElement = document.DocumentElement;
            var sdk = rootElement?.GetAttribute("Sdk");
            var frameworks = Values(document, "TargetFramework").Concat(Values(document, "TargetFrameworks")
                .SelectMany(value => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var projectDirectory = Path.GetDirectoryName(fullPath)!;
            var references = document.GetElementsByTagName("ProjectReference").OfType<XmlElement>()
                .Select(item => item.GetAttribute("Include"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Path.GetFullPath(Path.Combine(projectDirectory, value)))
                .Where(value => IsWithin(root, value))
                .Select(value => Path.GetRelativePath(root, value).Replace(Path.DirectorySeparatorChar, '/'))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var isTest = Values(document, "IsTestProject").Any(value =>
                    bool.TryParse(value, out var parsed) && parsed) ||
                relativePath.Contains("test", StringComparison.OrdinalIgnoreCase);
            return DomainResult.Success(new RepositoryProject(
                relativePath,
                Path.GetExtension(relativePath) switch { ".fsproj" => "F#", ".vbproj" => "Visual Basic", _ => "C#" },
                string.IsNullOrWhiteSpace(sdk) ? null : sdk,
                frameworks,
                references,
                isTest));
        }
        catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
        {
            return DomainResult.Fail<RepositoryProject>(new DomainFailure(
                FailureCode.ValidationFailure, "A project file is malformed or unreadable."));
        }
    }

    private static IEnumerable<string> Values(XmlDocument document, string localName) =>
        document.GetElementsByTagName(localName).OfType<XmlElement>()
            .Select(item => item.InnerText.Trim()).Where(value => value.Length > 0);

    private static string[] DetectBuildSystems(IReadOnlyList<string> files)
    {
        var systems = new HashSet<string>(StringComparer.Ordinal);
        if (files.Any(path => Path.GetExtension(path) is ".sln" or ".slnx" or ".csproj" or ".fsproj" or ".vbproj"))
        {
            systems.Add("MSBuild");
        }

        if (files.Contains("package.json", StringComparer.Ordinal)) systems.Add("npm");
        if (files.Any(path => Path.GetFileName(path) is "CMakeLists.txt")) systems.Add("CMake");
        if (files.Any(path => Path.GetFileName(path) is "Cargo.toml")) systems.Add("Cargo");
        if (files.Any(path => Path.GetFileName(path) is "pyproject.toml" or "setup.py")) systems.Add("Python");
        return systems.Order(StringComparer.Ordinal).ToArray();
    }

    private static string? LanguageFor(string path) => Path.GetExtension(path) switch
    {
        ".cs" => "C#",
        ".fs" => "F#",
        ".vb" => "Visual Basic",
        ".js" => "JavaScript",
        ".ts" or ".tsx" => "TypeScript",
        ".py" => "Python",
        ".rs" => "Rust",
        ".go" => "Go",
        _ => null,
    };

    private static bool IsInstruction(string path) => Path.GetFileName(path) is
        "AGENTS.md" or "CLAUDE.md" or "CONTRIBUTING.md" or "README.md" or ".editorconfig";

    private static bool IsContinuousIntegration(string path) =>
        path.StartsWith(".github/workflows/", StringComparison.Ordinal) ||
        Path.GetFileName(path) is ".gitlab-ci.yml" or "azure-pipelines.yml" or "Jenkinsfile";

    private static bool IsLockFile(string path) => Path.GetFileName(path) is
        "packages.lock.json" or "package-lock.json" or "pnpm-lock.yaml" or "yarn.lock" or
        "Cargo.lock" or "poetry.lock" or "uv.lock" or "packages.lock.json";

    private static bool TryRoot(string value, out string? root)
    {
        root = null;
        try
        {
            root = Path.GetFullPath(value);
            return Directory.Exists(root) && Path.IsPathFullyQualified(root) &&
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsWithin(string root, string path) => path.StartsWith(
        root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string FromSlash(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    private static string ComputeHash(RepositoryProfile profile)
    {
        var builder = new StringBuilder();
        Append(builder, profile.RootPath);
        foreach (var value in profile.SolutionFiles) Append(builder, value);
        foreach (var project in profile.Projects)
        {
            Append(builder, project.RelativePath); Append(builder, project.Language); Append(builder, project.Sdk ?? "");
            foreach (var value in project.TargetFrameworks) Append(builder, value);
            foreach (var value in project.ProjectReferences) Append(builder, value);
            Append(builder, project.IsTestProject);
        }

        foreach (var value in profile.Languages.Concat(profile.BuildSystems).Concat(profile.ContinuousIntegrationFiles)
                     .Concat(profile.LockFiles)) Append(builder, value);
        foreach (var instruction in profile.Instructions)
        {
            Append(builder, instruction.RelativePath); Append(builder, instruction.ContentHash); Append(builder, instruction.Length);
        }

        return Hash(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static void Append(StringBuilder builder, object value)
    {
        var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        builder.Append(text.Length).Append(':').Append(text).Append(';');
    }

    private static string Hash(ReadOnlySpan<byte> value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(value))}";

    private static DomainResult<RepositoryProfile> Failure(string message) =>
        DomainResult.Fail<RepositoryProfile>(new DomainFailure(FailureCode.ValidationFailure, message));
}
