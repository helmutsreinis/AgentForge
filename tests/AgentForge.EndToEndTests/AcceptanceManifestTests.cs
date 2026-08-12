using AgentForge.Release;
using System.Text.Json;
using System.Xml.Linq;

namespace AgentForge.EndToEndTests;

public sealed class AcceptanceManifestTests
{
    [Fact]
    public void R1_manifest_is_exact_complete_and_bound_to_passing_gates()
    {
        var root = FindRepositoryRoot();

        var manifest = AcceptanceEvidenceGenerator.LoadAndValidate(root);

        Assert.Equal(25, manifest.Scenarios.Count);
        Assert.Equal(Enumerable.Range(1, 25).Select(index => $"AC-{index:00}"),
            manifest.Scenarios.Select(item => item.Id));
        Assert.All(manifest.Scenarios, scenario =>
        {
            Assert.NotEmpty(scenario.Requirements);
            Assert.NotEmpty(scenario.Gates);
            Assert.NotEmpty(scenario.Tests);
            Assert.False(string.IsNullOrWhiteSpace(scenario.AuditEvidence));
            Assert.False(string.IsNullOrWhiteSpace(scenario.TrajectoryEvidence));
        });
    }

    [Fact]
    public void Evidence_generator_requires_every_result_and_archives_all_scenarios()
    {
        var root = FindRepositoryRoot();
        var manifest = AcceptanceEvidenceGenerator.LoadAndValidate(root);
        var results = Path.Combine(Path.GetTempPath(), $"agentforge-acceptance-results-{Guid.NewGuid():N}");
        var transcript = Path.Combine(results, "commands.txt");
        var output = Path.Combine(root, "artifacts", "acceptance", $"test-evidence-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(results);
        try
        {
            var elements = manifest.Scenarios.SelectMany(item => item.Tests).Distinct(StringComparer.Ordinal)
                .Select(selector => new XElement("UnitTestResult",
                    new XAttribute("testName", selector),
                    new XAttribute("outcome", "Passed")));
            new XDocument(new XElement("TestRun", new XElement("Results", elements)))
                .Save(Path.Combine(results, "acceptance.trx"));
            File.WriteAllText(transcript, "dotnet test AgentForge.slnx -c Release --no-build --logger trx");

            AcceptanceEvidenceGenerator.Generate(
                root,
                results,
                output,
                new string('a', 40),
                new DateTimeOffset(2026, 8, 12, 6, 0, 0, TimeSpan.Zero),
                transcript,
                ["win-x64-package-smoke", "linux-x64-package-smoke"]);

            using var document = JsonDocument.Parse(File.ReadAllBytes(output));
            Assert.Equal(25, document.RootElement.GetProperty("scenarios").GetArrayLength());
            Assert.All(document.RootElement.GetProperty("scenarios").EnumerateArray(), item =>
                Assert.Equal("Pass", item.GetProperty("status").GetString()));
            File.Delete(Path.Combine(results, "acceptance.trx"));
            Assert.Throws<InvalidDataException>(() => AcceptanceEvidenceGenerator.Generate(
                root,
                results,
                output + ".missing",
                new string('a', 40),
                new DateTimeOffset(2026, 8, 12, 6, 0, 0, TimeSpan.Zero),
                transcript,
                ["win-x64-package-smoke", "linux-x64-package-smoke"]));
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
            if (File.Exists(output + ".missing")) File.Delete(output + ".missing");
            if (Directory.Exists(results)) Directory.Delete(results, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AgentForge.slnx"))) return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("Repository root was not found.");
    }
}
