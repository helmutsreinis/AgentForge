using System.Text.Json;
using AgentForge.Release;

namespace AgentForge.EndToEndTests;

public sealed class ReleaseManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"agentforge-release-{Guid.NewGuid():N}");

    [Fact]
    public void Release_manifest_is_deterministic_complete_and_tamper_evident()
    {
        var repository = Path.Combine(_root, "repository");
        var release = Path.Combine(_root, "release");
        Directory.CreateDirectory(Path.Combine(repository, "src", "fixture"));
        Directory.CreateDirectory(Path.Combine(release, "win-x64", "host"));
        Directory.CreateDirectory(Path.Combine(release, "linux-x64", "host"));
        File.WriteAllText(Path.Combine(repository, "src", "fixture", "packages.lock.json"),
            """
            {"version":2,"dependencies":{"net10.0":{"Example.Package":{"type":"Direct","resolved":"1.2.3","contentHash":"fixture"}}}}
            """);
        File.WriteAllText(Path.Combine(release, "win-x64", "host", "AgentForge.Host.exe"), "windows");
        File.WriteAllText(Path.Combine(release, "linux-x64", "host", "AgentForge.Host"), "linux");

        var created = DateTimeOffset.Parse("2026-08-12T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        ReleaseManifestGenerator.Generate(release, repository, "1.0.0", new string('a', 40), created);
        Assert.Empty(ReleaseManifestGenerator.Verify(release));

        var checksum = File.ReadAllText(Path.Combine(release, "SHA256SUMS"));
        Assert.Contains("AgentForge.spdx.json", checksum, StringComparison.Ordinal);
        using var sbom = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(release, "AgentForge.spdx.json")));
        Assert.Equal("SPDX-2.3", sbom.RootElement.GetProperty("spdxVersion").GetString());
        Assert.Contains(sbom.RootElement.GetProperty("packages").EnumerateArray(),
            package => package.GetProperty("name").GetString() == "Example.Package");

        var zipA = Path.Combine(_root, "package-a.zip");
        var zipB = Path.Combine(_root, "package-b.zip");
        ReleasePackageBuilder.CreateArchive(Path.Combine(release, "win-x64"), zipA, "zip", created);
        ReleasePackageBuilder.CreateArchive(Path.Combine(release, "win-x64"), zipB, "zip", created);
        Assert.Equal(File.ReadAllBytes(zipA), File.ReadAllBytes(zipB));

        File.AppendAllText(Path.Combine(release, "linux-x64", "host", "AgentForge.Host"), "tamper");
        Assert.Contains(ReleaseManifestGenerator.Verify(release),
            error => error.Contains("Checksum mismatch", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Service_container_and_release_assets_preserve_single_operator_security_defaults()
    {
        var repository = FindRepositoryRoot();
        var dockerfile = File.ReadAllText(Path.Combine(repository, "Dockerfile"));
        Assert.Contains("USER $APP_UID", dockerfile, StringComparison.Ordinal);
        Assert.Contains("AGENTFORGE_ENDPOINT=http://127.0.0.1:5047", dockerfile, StringComparison.Ordinal);
        Assert.Contains("health-probe", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0.0", dockerfile, StringComparison.Ordinal);
        var sandboxDockerfile = File.ReadAllText(Path.Combine(
            repository, "packaging", "container", "Dockerfile.sandbox"));
        Assert.Contains("USER $APP_UID", sandboxDockerfile, StringComparison.Ordinal);
        Assert.Contains("DOTNET_CLI_HOME=/tmp", sandboxDockerfile, StringComparison.Ordinal);
        var systemd = File.ReadAllText(Path.Combine(repository, "packaging", "linux", "agentforge.service"));
        Assert.Contains("NoNewPrivileges=true", systemd, StringComparison.Ordinal);
        Assert.Contains("ProtectSystem=strict", systemd, StringComparison.Ordinal);
        Assert.Contains("WantedBy=default.target", systemd, StringComparison.Ordinal);
        Assert.DoesNotContain("User=root", systemd, StringComparison.Ordinal);
        var windows = File.ReadAllText(Path.Combine(repository, "packaging", "windows", "install-service.ps1"));
        Assert.Contains("PSCredential", windows, StringComparison.Ordinal);
        Assert.Contains("DPAPI remains recoverable", windows, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalSystem", windows, StringComparison.OrdinalIgnoreCase);
        var workflow = File.ReadAllText(Path.Combine(repository, ".github", "workflows", "release.yml"));
        Assert.Contains("attest-build-provenance", workflow, StringComparison.Ordinal);
        Assert.Contains("sbom: true", workflow, StringComparison.Ordinal);
        Assert.Contains("DockerSandboxLiveTests", workflow, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AgentForge.slnx")))
            current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
