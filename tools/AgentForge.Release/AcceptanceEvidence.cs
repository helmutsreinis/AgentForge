using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AgentForge.Release;

public sealed record AcceptanceManifest(
    int SchemaVersion,
    string Source,
    IReadOnlyList<string> ConfigurationFiles,
    IReadOnlyList<AcceptanceScenario> Scenarios);

public sealed record AcceptanceScenario(
    string Id,
    string Title,
    IReadOnlyList<string> Requirements,
    IReadOnlyList<string> Gates,
    IReadOnlyList<string> Tests,
    IReadOnlyList<string>? AllowSkippedTests,
    IReadOnlyList<string>? ExternalEvidence,
    string AuditEvidence,
    string TrajectoryEvidence);

public static partial class AcceptanceEvidenceGenerator
{
    private const int MaximumManifestBytes = 512 * 1024;
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static AcceptanceManifest LoadAndValidate(string repositoryRoot)
    {
        var root = ExistingDirectory(repositoryRoot);
        var manifestPath = Path.Combine(root, "artifacts", "acceptance", "R1-scenarios.json");
        var manifestBytes = ReadBoundedFile(manifestPath, MaximumManifestBytes);
        var manifest = JsonSerializer.Deserialize<AcceptanceManifest>(manifestBytes, ReadOptions)
            ?? throw new InvalidDataException("Acceptance manifest is empty.");
        if (manifest.SchemaVersion != 1 || !IsBounded(manifest.Source, 256) ||
            manifest.ConfigurationFiles is null || manifest.Scenarios is null || manifest.Scenarios.Count != 25)
            throw new InvalidDataException("Acceptance manifest schema, source, or scenario count is invalid.");

        var expectedIds = Enumerable.Range(1, 25).Select(index => $"AC-{index:00}").ToArray();
        if (!manifest.Scenarios.Select(item => item.Id).SequenceEqual(expectedIds, StringComparer.Ordinal))
            throw new InvalidDataException("Acceptance scenario IDs must be the ordered AC-01 through AC-25 set.");
        if (manifest.ConfigurationFiles.Count is < 1 or > 16 ||
            manifest.ConfigurationFiles.Distinct(StringComparer.Ordinal).Count() != manifest.ConfigurationFiles.Count)
            throw new InvalidDataException("Acceptance configuration files are invalid or duplicated.");
        foreach (var relativePath in manifest.ConfigurationFiles)
            ValidateRepositoryFile(root, relativePath, 4 * 1024 * 1024);

        var requirements = File.ReadAllText(Path.Combine(root, "docs", "REQUIREMENTS.md"));
        foreach (var scenario in manifest.Scenarios)
        {
            if (!ScenarioIdPattern().IsMatch(scenario.Id) || !IsBounded(scenario.Title, 256) ||
                !IsBounded(scenario.AuditEvidence, 1024) || !IsBounded(scenario.TrajectoryEvidence, 1024) ||
                scenario.Requirements is null || scenario.Requirements.Count is < 1 or > 16 ||
                scenario.Gates is null || scenario.Gates.Count is < 1 or > 8 ||
                scenario.Tests is null || scenario.Tests.Count is < 1 or > 16)
                throw new InvalidDataException($"Acceptance scenario {scenario.Id} is incomplete or unbounded.");
            EnsureUnique(scenario.Requirements, scenario.Id, "requirement");
            EnsureUnique(scenario.Gates, scenario.Id, "gate");
            EnsureUnique(scenario.Tests, scenario.Id, "test");
            foreach (var requirement in scenario.Requirements)
            {
                if (!RequirementIdPattern().IsMatch(requirement) ||
                    !requirements.Contains($"| {requirement} |", StringComparison.Ordinal))
                    throw new InvalidDataException($"Scenario {scenario.Id} references an unknown requirement.");
            }

            foreach (var gate in scenario.Gates)
            {
                if (Path.GetFileName(gate) != gate || !gate.EndsWith(".md", StringComparison.Ordinal))
                    throw new InvalidDataException($"Scenario {scenario.Id} gate identity is invalid.");
                var gateBytes = ReadBoundedFile(Path.Combine(root, "artifacts", "gates", gate), 1024 * 1024);
                var gateText = Encoding.UTF8.GetString(gateBytes);
                if (!gateText.Contains("Status: **Pass**", StringComparison.Ordinal) &&
                    !gateText.Contains("Decision: **Pass**", StringComparison.Ordinal))
                    throw new InvalidDataException($"Scenario {scenario.Id} references a gate that did not pass.");
            }

            foreach (var test in scenario.Tests)
            {
                if (!TestSelectorPattern().IsMatch(test))
                    throw new InvalidDataException($"Scenario {scenario.Id} has an invalid test selector.");
            }

            var skipped = scenario.AllowSkippedTests ?? [];
            if (skipped.Any(item => !scenario.Tests.Contains(item, StringComparer.Ordinal)))
                throw new InvalidDataException($"Scenario {scenario.Id} permits skipping an unrelated test.");
            var external = scenario.ExternalEvidence ?? [];
            if (external.Any(item => !ExternalEvidencePattern().IsMatch(item)) ||
                external.Distinct(StringComparer.Ordinal).Count() != external.Count)
                throw new InvalidDataException($"Scenario {scenario.Id} external evidence is invalid.");
        }

        return manifest;
    }

    public static void Generate(
        string repositoryRoot,
        string resultsDirectory,
        string outputPath,
        string commit,
        DateTimeOffset created,
        string transcriptPath,
        IReadOnlyCollection<string> passedExternalEvidence)
    {
        if (!CommitPattern().IsMatch(commit)) throw new ArgumentException("Acceptance commit must be a full Git SHA-1.");
        var root = ExistingDirectory(repositoryRoot);
        var resultsRoot = ExistingDirectory(resultsDirectory);
        var manifest = LoadAndValidate(root);
        var results = LoadResults(resultsRoot);
        var transcript = ReadTranscript(transcriptPath);
        var external = passedExternalEvidence.ToHashSet(StringComparer.Ordinal);
        var requiredExternal = manifest.Scenarios.SelectMany(item => item.ExternalEvidence ?? []).ToHashSet(StringComparer.Ordinal);
        if (!external.SetEquals(requiredExternal))
            throw new InvalidDataException("Passed external evidence must exactly match the manifest requirement.");

        var configurationHashes = manifest.ConfigurationFiles.ToDictionary(
            item => item,
            item => HashFile(Path.Combine(root, item)),
            StringComparer.Ordinal);
        var configurationHash = HashJson(configurationHashes);
        var transcriptHash = HashJson(transcript);
        var manifestHash = HashFile(Path.Combine(root, "artifacts", "acceptance", "R1-scenarios.json"));
        var scenarioEvidence = new List<object>(manifest.Scenarios.Count);
        foreach (var scenario in manifest.Scenarios)
        {
            var permittedSkipped = (scenario.AllowSkippedTests ?? []).ToHashSet(StringComparer.Ordinal);
            var testEvidence = new List<object>(scenario.Tests.Count);
            foreach (var selector in scenario.Tests)
            {
                var matching = results.Where(item => string.Equals(item.TestName, selector, StringComparison.Ordinal) ||
                    item.TestName.StartsWith(selector + "(", StringComparison.Ordinal)).ToArray();
                if (matching.Length == 0)
                    throw new InvalidDataException($"Acceptance test evidence is missing for {selector}.");
                var outcomes = matching.Select(item => item.Outcome).Distinct(StringComparer.Ordinal).ToArray();
                var passed = outcomes.All(item => item == "Passed") ||
                    permittedSkipped.Contains(selector) && outcomes.All(item => item is "Passed" or "NotExecuted" or "Skipped");
                if (!passed) throw new InvalidDataException($"Acceptance test did not pass: {selector}.");
                testEvidence.Add(new
                {
                    selector,
                    outcomes,
                    resultFiles = matching.Select(item => item.ResultFileHash).Distinct(StringComparer.Ordinal).Order().ToArray(),
                });
            }

            var gateHashes = scenario.Gates.ToDictionary(
                item => item,
                item => HashFile(Path.Combine(root, "artifacts", "gates", item)),
                StringComparer.Ordinal);
            scenarioEvidence.Add(new
            {
                scenario.Id,
                status = "Pass",
                scenario.Requirements,
                sourceCommit = commit,
                configurationHash,
                manifestHash,
                transcriptHash,
                testEvidence,
                gateHashes,
                externalEvidence = scenario.ExternalEvidence ?? [],
                auditReference = scenario.AuditEvidence,
                trajectoryReference = scenario.TrajectoryEvidence,
                verifier = "AgentForge.Release.AcceptanceEvidenceGenerator/v1",
            });
        }

        var output = Path.GetFullPath(outputPath);
        var outputParent = Path.GetDirectoryName(output);
        if (outputParent is null || !Directory.Exists(outputParent) || File.Exists(output))
            throw new ArgumentException("Acceptance output parent must exist and output must be new.");
        if (!output.StartsWith(root + Path.DirectorySeparatorChar, PathComparison()))
            throw new ArgumentException("Acceptance output must remain inside the repository.");
        var document = new
        {
            schemaVersion = 1,
            generatedAt = created.ToUniversalTime(),
            sourceCommit = commit,
            manifestHash,
            configurationHashes,
            configurationHash,
            commandTranscript = transcript,
            transcriptHash,
            externalEvidence = external.Order(StringComparer.Ordinal).ToArray(),
            scenarios = scenarioEvidence,
        };
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, document, WriteOptions);
    }

    private static List<TestResultEvidence> LoadResults(string resultsRoot)
    {
        var result = new List<TestResultEvidence>();
        foreach (var path in Directory.EnumerateFiles(resultsRoot, "*.trx", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            var bytes = ReadBoundedFile(path, 64 * 1024 * 1024);
            var hash = Hash(bytes);
            using var xml = new MemoryStream(bytes, writable: false);
            var document = XDocument.Load(xml, LoadOptions.None);
            foreach (var element in document.Descendants().Where(item => item.Name.LocalName == "UnitTestResult"))
            {
                var name = element.Attribute("testName")?.Value;
                var outcome = element.Attribute("outcome")?.Value;
                if (IsBounded(name, 2048) && IsBounded(outcome, 64))
                    result.Add(new TestResultEvidence(name!, outcome!, hash));
            }
        }
        return result.Count == 0
            ? throw new InvalidDataException("No bounded TRX test results were found.")
            : result;
    }

    private static string[] ReadTranscript(string path)
    {
        var bytes = ReadBoundedFile(path, 128 * 1024);
        var lines = Encoding.UTF8.GetString(bytes).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length is < 1 or > 128 || lines.Any(item => !IsBounded(item, 2048)))
            throw new InvalidDataException("Acceptance command transcript is missing or unbounded.");
        return lines;
    }

    private static void ValidateRepositoryFile(string root, string relativePath, int maximumBytes)
    {
        if (relativePath.Length == 0 || Path.IsPathRooted(relativePath) ||
            relativePath.Contains("..", StringComparison.Ordinal) || relativePath.Contains('\\') ||
            relativePath[0] == '.')
            throw new InvalidDataException("Acceptance configuration path is unsafe.");
        _ = ReadBoundedFile(Path.Combine(root, relativePath), maximumBytes);
    }

    private static byte[] ReadBoundedFile(string path, int maximumBytes)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.LinkTarget is not null || info.Length is < 1 || info.Length > maximumBytes)
            throw new InvalidDataException("Acceptance evidence file is missing, linked, empty, or oversized.");
        return File.ReadAllBytes(fullPath);
    }

    private static string ExistingDirectory(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var info = new DirectoryInfo(fullPath);
        if (!info.Exists || info.LinkTarget is not null) throw new ArgumentException("Acceptance directory is unavailable or linked.");
        return fullPath;
    }

    private static void EnsureUnique(IReadOnlyList<string> values, string scenarioId, string kind)
    {
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new InvalidDataException($"Scenario {scenarioId} has duplicate {kind} evidence.");
    }

    private static string HashFile(string path) => Hash(File.ReadAllBytes(path));

    private static string HashJson<T>(T value) => Hash(JsonSerializer.SerializeToUtf8Bytes(value, WriteOptions));

    private static string Hash(byte[] bytes) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    private static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [GeneratedRegex("^AC-(0[1-9]|1[0-9]|2[0-5])$", RegexOptions.CultureInvariant)]
    private static partial Regex ScenarioIdPattern();

    [GeneratedRegex("^AF-[A-Z]+-[0-9]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex RequirementIdPattern();

    [GeneratedRegex("^AgentForge\\.(UnitTests|IntegrationTests|ArchitectureTests|SecurityTests|CrossPlatformTests|EndToEndTests)\\.[A-Za-z0-9_]+\\.[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TestSelectorPattern();

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ExternalEvidencePattern();

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    private sealed record TestResultEvidence(string TestName, string Outcome, string ResultFileHash);
}
