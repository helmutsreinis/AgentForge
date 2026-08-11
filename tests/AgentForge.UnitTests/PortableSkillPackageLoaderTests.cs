using System.Text;
using AgentForge.Abstractions.Skills;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Skills;
using AgentForge.Skills;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.UnitTests;

public sealed class PortableSkillPackageLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"agentforge-skill-{Guid.NewGuid():N}");

    [Fact]
    public async Task Portable_package_is_strictly_loaded_hashed_and_reproducible()
    {
        var directory = CreatePackage("skill:csharp.review", "1.2.3", signature: null);
        File.WriteAllText(Path.Combine(directory, "references.md"), "bounded reference", new UTF8Encoding(false));
        var loader = CreateLoader();

        var first = await loader.LoadAsync(directory, CancellationToken.None);
        File.SetLastWriteTimeUtc(Path.Combine(directory, "SKILL.md"), DateTime.UtcNow.AddDays(-10));
        var second = await loader.LoadAsync(directory, CancellationToken.None);

        Assert.True(first.IsSuccess, first.Failure?.Message);
        Assert.True(second.IsSuccess, second.Failure?.Message);
        Assert.Equal("skill:csharp.review", first.Value.Package.Id.Value);
        Assert.Equal("1.2.3", first.Value.Package.Version.Value);
        Assert.Equal(first.Value.Package.PackageHash, second.Value.Package.PackageHash);
        Assert.Equal(first.Value.CanonicalBytes.ToArray(), second.Value.CanonicalBytes.ToArray());
        Assert.Equal(3, first.Value.Package.FileHashes.Count);
        Assert.True(SkillPackageValidator.Validate(first.Value.Package).IsSuccess);
    }

    [Fact]
    public async Task Signed_package_verifies_unsigned_canonical_payload_and_default_denies()
    {
        var directory = CreatePackage(
            "skill:signed.fixture",
            "2.0.0",
            new { algorithm = "ed25519", keyId = "fixture-key", value = "fixture-signature" });
        var verifier = new RecordingVerifier();
        var accepted = await CreateLoader(verifier).LoadAsync(directory, CancellationToken.None);

        Assert.True(accepted.IsSuccess, accepted.Failure?.Message);
        Assert.NotNull(verifier.Request);
        Assert.NotEqual(accepted.Value.Package.PackageHash, verifier.Request!.PackageHash);
        Assert.Equal("ed25519", verifier.Request.Algorithm);

        var rejected = await CreateLoader().LoadAsync(directory, CancellationToken.None);
        Assert.False(rejected.IsSuccess);
        Assert.Equal(FailureCode.UnsupportedCapability, rejected.Failure!.Code);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0+")]
    public void Invalid_semantic_versions_are_rejected(string value)
    {
        Assert.False(SkillVersion.TryParse(value, out _));
    }

    [Fact]
    public void Semantic_version_precedence_is_not_lexical()
    {
        Assert.True(SkillVersion.TryParse("1.10.0", out var ten));
        Assert.True(SkillVersion.TryParse("1.2.0", out var two));
        Assert.True(SkillVersion.TryParse("1.0.0-alpha.1", out var prerelease));
        Assert.True(SkillVersion.TryParse("1.0.0", out var release));
        Assert.True(ten > two);
        Assert.True(release > prerelease);
    }

    [Fact]
    public async Task Duplicate_json_unknown_schema_invalid_utf8_and_oversize_fail_closed()
    {
        var duplicate = CreatePackage("skill:duplicate", "1.0.0", null);
        var path = Path.Combine(duplicate, "skill.harness.json");
        var json = File.ReadAllText(path);
        File.WriteAllText(path, json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1",
            StringComparison.Ordinal));
        Assert.False((await CreateLoader().LoadAsync(duplicate, CancellationToken.None)).IsSuccess);

        var unknown = CreatePackage("skill:unknown", "1.0.0", null);
        path = Path.Combine(unknown, "skill.harness.json");
        json = File.ReadAllText(path);
        File.WriteAllText(path, json.Replace("\"signature\":null", "\"unknown\":true,\"signature\":null",
            StringComparison.Ordinal));
        Assert.False((await CreateLoader().LoadAsync(unknown, CancellationToken.None)).IsSuccess);

        var invalidUtf8 = CreatePackage("skill:utf8", "1.0.0", null);
        File.WriteAllBytes(Path.Combine(invalidUtf8, "SKILL.md"), [0xc3, 0x28]);
        Assert.False((await CreateLoader().LoadAsync(invalidUtf8, CancellationToken.None)).IsSuccess);

        var oversized = CreatePackage("skill:large", "1.0.0", null);
        File.WriteAllBytes(Path.Combine(oversized, "large.bin"), new byte[1_048_577]);
        Assert.False((await CreateLoader().LoadAsync(oversized, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task Linked_file_or_directory_is_rejected_when_platform_supports_links()
    {
        var outside = Path.Combine(_root, "outside.md");
        Directory.CreateDirectory(_root);
        File.WriteAllText(outside, "outside");
        var directory = CreatePackage("skill:linked", "1.0.0", null);
        var link = Path.Combine(directory, "linked.md");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var result = await CreateLoader().LoadAsync(directory, CancellationToken.None);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Dependency_graph_requires_exact_versions_and_rejects_cycles()
    {
        var one = Package("skill:one", "1.0.0", new SkillDependency(new SkillId("skill:two"), Version("1.0.0")));
        var two = Package("skill:two", "1.0.0", new SkillDependency(new SkillId("skill:one"), Version("1.0.0")));
        var cycle = SkillPackageValidator.ValidateDependencyGraph([one, two]);
        Assert.False(cycle.IsSuccess);

        var missing = SkillPackageValidator.ValidateDependencyGraph([one]);
        Assert.False(missing.IsSuccess);

        var leaf = two with { Dependencies = [] };
        Assert.True(SkillPackageValidator.ValidateDependencyGraph([one, leaf]).IsSuccess);
    }

    private string CreatePackage(string id, string version, object? signature)
    {
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), "# Fixture\n\nFollow the bounded procedure.\n",
            new UTF8Encoding(false));
        var signatureJson = signature is null
            ? "null"
            : "{\"algorithm\":\"ed25519\",\"keyId\":\"fixture-key\",\"value\":\"fixture-signature\"}";
        File.WriteAllText(
            Path.Combine(directory, "skill.harness.json"),
            "{" +
            "\"schemaVersion\":1," +
            $"\"id\":\"{id}\"," +
            $"\"version\":\"{version}\"," +
            "\"description\":\"Fixture skill\"," +
            "\"dependencies\":[]," +
            "\"requirements\":{" +
            "\"operatingSystems\":[\"windows\",\"linux\"]," +
            "\"modelCapabilities\":[\"text\"]," +
            "\"tools\":[\"tool:repo.read\"]}," +
            "\"permissions\":[\"repo:read\"]," +
            $"\"signature\":{signatureJson}" +
            "}",
            new UTF8Encoding(false));
        return directory;
    }

    private static ISkillPackageLoader CreateLoader(ISkillSignatureVerifier? verifier = null)
    {
        var services = new ServiceCollection();
        if (verifier is not null)
        {
            services.AddSingleton(verifier);
            services.AddSingleton<ISkillSignatureVerifier>(verifier);
        }

        services.AddAgentForgeSkills();
        return services.BuildServiceProvider().GetRequiredService<ISkillPackageLoader>();
    }

    private static SkillPackage Package(string id, string version, params SkillDependency[] dependencies) => new(
        new SkillId(id),
        Version(version),
        "Fixture",
        "# Fixture",
        dependencies,
        new SkillRequirements(["linux"], ["text"], ["tool:repo.read"]),
        ["repo:read"],
        new Dictionary<string, string>
        {
            ["SKILL.md"] = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ["skill.harness.json"] = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        },
        "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
        null);

    private static SkillVersion Version(string value)
    {
        Assert.True(SkillVersion.TryParse(value, out var version));
        return version;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RecordingVerifier : ISkillSignatureVerifier
    {
        public SkillSignatureVerificationRequest? Request { get; private set; }

        public DomainResult<bool> Verify(SkillSignatureVerificationRequest request)
        {
            Request = request;
            return DomainResult.Success(true);
        }
    }
}
