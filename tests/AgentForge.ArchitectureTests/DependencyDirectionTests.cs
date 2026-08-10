using System.Xml.Linq;

namespace AgentForge.ArchitectureTests;

public sealed class DependencyDirectionTests
{
    [Fact]
    public void DomainHasNoProjectOrPackageDependencies()
    {
        var project = LoadProject("src", "AgentForge.Domain", "AgentForge.Domain.csproj");

        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.Empty(project.Descendants("PackageReference"));
    }

    [Fact]
    public void AbstractionsDependsOnlyOnDomain()
    {
        var project = LoadProject("src", "AgentForge.Abstractions", "AgentForge.Abstractions.csproj");
        var references = project.Descendants("ProjectReference")
            .Select(reference => GetProjectName(reference.Attribute("Include")?.Value))
            .Where(reference => reference is not null)
            .Select(reference => reference!)
            .ToArray();

        Assert.Equal(["AgentForge.Domain"], references);
    }

    [Fact]
    public void FeatureModulesDoNotReferenceOtherFeatureImplementations()
    {
        var root = FindRepositoryRoot();
        var compositionRoots = new HashSet<string>(StringComparer.Ordinal)
        {
            "AgentForge.Host",
            "AgentForge.Cli",
        };
        var allowedFeatureDependencies = new HashSet<string>(StringComparer.Ordinal)
        {
            "AgentForge.Domain",
            "AgentForge.Abstractions",
        };

        var violations = new List<string>();
        foreach (var projectPath in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            if (compositionRoots.Contains(projectName) || allowedFeatureDependencies.Contains(projectName))
            {
                continue;
            }

            var project = XDocument.Load(projectPath);
            foreach (var reference in project.Descendants("ProjectReference"))
            {
                var referencedName = GetProjectName(reference.Attribute("Include")?.Value);
                if (referencedName is not null && !allowedFeatureDependencies.Contains(referencedName))
                {
                    violations.Add($"{projectName} -> {referencedName}");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void EnvironmentInventoryContainsNoProcessExecutionPrimitive()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "AgentForge.Environment",
            "SystemEnvironmentProfiler.cs"));

        Assert.DoesNotContain("System.Diagnostics.Process", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessStartInfo", source, StringComparison.Ordinal);
    }

    private static XDocument LoadProject(params string[] relativeSegments) =>
        XDocument.Load(Path.Combine([FindRepositoryRoot(), .. relativeSegments]));

    private static string? GetProjectName(string? projectReference)
    {
        if (projectReference is null)
        {
            return null;
        }

        var normalized = projectReference.Replace('\\', '/');
        return Path.GetFileNameWithoutExtension(normalized);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentForge.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the AgentForge repository root.");
    }
}
