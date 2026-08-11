using System.Security.Cryptography;
using System.Text;
using AgentForge.Abstractions.Coding;
using AgentForge.Abstractions.Time;
using AgentForge.Abstractions.Tools;
using AgentForge.Coding;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Coding;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.UnitTests;

public sealed class CodingPatchAndVerificationTests : IDisposable
{
    private const string GitHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string EvidenceHash =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"agentforge-patch-{Guid.NewGuid():N}");

    [Fact]
    public async Task Hash_bound_patch_changes_only_exact_target_and_conflicting_replay_denies()
    {
        Directory.CreateDirectory(_root);
        const string before = "namespace Sample;\npublic static class Calculator\n{\n    public static int Add(int left, int right) => left - right;\n}\n";
        var path = Path.Combine(_root, "Calculator.cs");
        await File.WriteAllTextAsync(path, before, new UTF8Encoding(false));
        var patch = CodingPatchValidator.Create(GitHash,
        [
            new CodingFilePatch(
                "Calculator.cs",
                Hash(Encoding.UTF8.GetBytes(before)),
                "--- a/Calculator.cs\n+++ b/Calculator.cs\n@@ -1,5 +1,5 @@\n namespace Sample;\n public static class Calculator\n {\n-    public static int Add(int left, int right) => left - right;\n+    public static int Add(int left, int right) => left + right;\n }\n"),
        ]);
        Assert.True(patch.IsSuccess);
        await using var services = BuildServices(new RecordingSandbox());
        var applier = services.GetRequiredService<ICodingPatchApplier>();
        var result = await applier.ApplyAsync(Workspace(), patch.Value, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Contains("left + right", await File.ReadAllTextAsync(path));
        Assert.Equal(1, result.Value.Files.Single().AddedLines);
        Assert.Equal(1, result.Value.Files.Single().RemovedLines);
        Assert.Equal(patch.Value.PatchHash, result.Value.PatchHash);
        Assert.StartsWith("sha256:", result.Value.ReceiptHash, StringComparison.Ordinal);

        var replay = await applier.ApplyAsync(Workspace(), patch.Value, CancellationToken.None);
        Assert.False(replay.IsSuccess);
        Assert.Equal(FailureCode.ConcurrencyConflict, replay.Failure?.Code);
    }

    [Fact]
    public async Task Invalid_second_file_context_leaves_every_file_unchanged()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "one.txt"), "one\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "two.txt"), "two\n");
        var patch = CodingPatchValidator.Create(GitHash,
        [
            new CodingFilePatch("one.txt", Hash("one\n"),
                "--- a/one.txt\n+++ b/one.txt\n@@ -1 +1 @@\n-one\n+ONE\n"),
            new CodingFilePatch("two.txt", Hash("two\n"),
                "--- a/two.txt\n+++ b/two.txt\n@@ -1 +1 @@\n-not-two\n+TWO\n"),
        ]);
        Assert.True(patch.IsSuccess);
        await using var services = BuildServices(new RecordingSandbox());
        var result = await services.GetRequiredService<ICodingPatchApplier>()
            .ApplyAsync(Workspace(), patch.Value, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("one\n", await File.ReadAllTextAsync(Path.Combine(_root, "one.txt")));
        Assert.Equal("two\n", await File.ReadAllTextAsync(Path.Combine(_root, "two.txt")));
    }

    [Fact]
    public async Task Verification_rechecks_authority_requires_container_and_hashes_outputs()
    {
        Directory.CreateDirectory(_root);
        var sandbox = new RecordingSandbox();
        await using var services = BuildServices(sandbox);
        await using var scope = services.CreateAsyncScope();
        var workspace = Workspace();
        var authority = Authority(workspace);
        var executable = Path.Combine(_root, OperatingSystem.IsWindows() ? "fixture.exe" : "fixture");
        var plan = CodingPatchValidator.CreateVerificationPlan(
        [
            new CodingVerificationCommand(
                CodingVerificationKind.Build, executable, ["build", "--locked-mode"], ".",
                new Dictionary<string, string> { ["DOTNET_NOLOGO"] = "1" }, TimeSpan.FromMinutes(2), 65_536,
                ProcessSandboxKind.Container, ProcessNetworkPolicy.Denied, true),
            new CodingVerificationCommand(
                CodingVerificationKind.Test, executable, ["test", "--no-build"], ".",
                new Dictionary<string, string>(), TimeSpan.FromMinutes(2), 65_536,
                ProcessSandboxKind.Container, ProcessNetworkPolicy.Denied, true),
        ]);
        Assert.True(plan.IsSuccess);
        var verifier = scope.ServiceProvider.GetRequiredService<ICodingVerifier>();
        var result = await verifier.VerifyAsync(
            workspace, authority, plan.Value, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.True(result.Value.Passed);
        Assert.Equal(2, result.Value.Results.Count);
        Assert.Equal(2, sandbox.Requests.Count);
        Assert.All(sandbox.Requests, request =>
        {
            Assert.Equal(ProcessSandboxKind.Container, request.RequiredSandbox);
            Assert.Equal(ProcessNetworkPolicy.Denied, request.NetworkPolicy);
            Assert.True(request.RequiredFeatures.HasFlag(ProcessIsolationFeature.FileSystemIsolation));
            Assert.True(request.RequiredFeatures.HasFlag(ProcessIsolationFeature.NetworkIsolation));
        });
        Assert.DoesNotContain("fixture output", result.Value.Results[0].StandardOutputHash, StringComparison.Ordinal);

        var unsafePlan = CodingPatchValidator.CreateVerificationPlan(
        [
            plan.Value.Commands[0] with
            {
                RequiredSandbox = ProcessSandboxKind.RestrictedHost,
                NetworkPolicy = ProcessNetworkPolicy.InheritHost,
            },
        ]);
        Assert.True(unsafePlan.IsSuccess);
        var denied = await verifier.VerifyAsync(
            workspace, authority, unsafePlan.Value, CancellationToken.None);
        Assert.False(denied.IsSuccess);
        Assert.Equal(FailureCode.UnsupportedCapability, denied.Failure?.Code);
        Assert.Equal(2, sandbox.Requests.Count);
    }

    [Fact]
    public async Task Backend_catalog_exposes_patch_only_contract_and_publish_requires_approval()
    {
        Directory.CreateDirectory(_root);
        var sandbox = new RecordingSandbox();
        await using var services = BuildServices(sandbox, new DeterministicBackend());
        await using var scope = services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ICodingBackendCatalog>();
        var descriptor = await catalog.DescribeAsync("backend:deterministic", "1.0.0", CancellationToken.None);
        Assert.True(descriptor.IsSuccess);
        Assert.True(descriptor.Value.SupportsPatchProposal);
        Assert.True(descriptor.Value.IsExternal);
        Assert.False((await catalog.ResolveAsync("backend:missing", "1.0.0", CancellationToken.None)).IsSuccess);

        var workspace = Workspace();
        var executable = Path.Combine(_root, OperatingSystem.IsWindows() ? "publish.exe" : "publish");
        var plan = CodingPatchValidator.CreateVerificationPlan(
        [
            new CodingVerificationCommand(CodingVerificationKind.Publish, executable, ["publish"], ".",
                new Dictionary<string, string>(), TimeSpan.FromMinutes(1), 16_384,
                ProcessSandboxKind.Container, ProcessNetworkPolicy.Denied, true),
        ]);
        Assert.True(plan.IsSuccess);
        var denied = await scope.ServiceProvider.GetRequiredService<ICodingVerifier>().VerifyAsync(
            workspace, Authority(workspace), plan.Value, CancellationToken.None);
        Assert.False(denied.IsSuccess);
        Assert.Equal(FailureCode.ApprovalRequired, denied.Failure?.Code);
        Assert.Empty(sandbox.Requests);
    }

    private static ServiceProvider BuildServices(RecordingSandbox sandbox, params ICodingBackend[] backends)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new FixedClock(new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero)));
        services.AddSingleton<ISandbox>(sandbox);
        foreach (var backend in backends) services.AddSingleton<ICodingBackend>(backend);
        services.AddAgentForgeCoding();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private CodingWorkspace Workspace() => new(
        new CodingSessionId(Guid.Parse("6cd378f6-d180-44e2-9f03-ad76d12fbb96")),
        _root,
        _root,
        GitHash,
        GitHash,
        "codex/fixture",
        true,
        new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero));

    private static CodingAuthoritySnapshot Authority(CodingWorkspace workspace) => new(
        new InstallationId(Guid.Parse("7ca87fd1-d1ac-4814-8489-847de789cdbe")),
        new AgentIdentityId(Guid.Parse("e018a6d1-3d8c-4d92-b67b-47866abb0f7a")),
        1, EvidenceHash, EvidenceHash, EvidenceHash, EvidenceHash,
        CodingRecordValidator.ComputeWorkspaceHash(workspace), new ActorId("operator"),
        new CorrelationId("coding-verify"), null);

    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));

    private static string Hash(ReadOnlySpan<byte> value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(value))}";

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class RecordingSandbox : ISandbox
    {
        public List<ProcessExecutionRequest> Requests { get; } = [];

        public ProcessSandboxCapabilities Capabilities => new(
            ProcessSandboxKind.Container,
            true,
            ProcessIsolationFeature.DirectExecutable | ProcessIsolationFeature.ArgumentArray |
            ProcessIsolationFeature.EnvironmentAllowlist | ProcessIsolationFeature.WorkingDirectoryContainment |
            ProcessIsolationFeature.BoundedOutput | ProcessIsolationFeature.WallClockTimeout |
            ProcessIsolationFeature.ProcessTreeTermination | ProcessIsolationFeature.NetworkIsolation |
            ProcessIsolationFeature.FileSystemIsolation,
            "deterministic isolated fixture");

        public Task<DomainResult<ProcessExecutionResult>> ExecuteAsync(
            ProcessExecutionRequest request,
            IProcessOutputObserver? observer,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var started = new DateTimeOffset(2026, 8, 12, 12, 0, Requests.Count, TimeSpan.Zero);
            return Task.FromResult(DomainResult.Success(new ProcessExecutionResult(
                0, Encoding.UTF8.GetBytes("fixture output"), [], started, started.AddSeconds(1),
                TimeSpan.FromSeconds(1), Capabilities)));
        }
    }

    private sealed class DeterministicBackend : ICodingBackend
    {
        public CodingBackendDescriptor Descriptor => new(
            "backend:deterministic", "1.0.0", true, ["C#"], true, true);

        public Task<DomainResult<CodingBackendProposal>> ProposeAsync(
            CodingBackendRequest request,
            CancellationToken cancellationToken) => Task.FromResult(DomainResult.Fail<CodingBackendProposal>(
                new DomainFailure(FailureCode.UnsupportedCapability, "Not invoked by this catalog fixture.")));
    }
}
