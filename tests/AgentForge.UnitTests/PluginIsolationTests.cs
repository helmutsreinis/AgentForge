using System.Security.Cryptography;
using System.Text.Json;
using AgentForge.Abstractions.Plugins;
using AgentForge.Abstractions.Setup;
using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Plugins;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Setup;
using AgentForge.Domain.Tools;
using AgentForge.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.UnitTests;

public sealed class PluginIsolationTests : IDisposable
{
    private static readonly string[] InventoryPermission = ["inventory:read"];
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"agentforge-plugins-{Guid.NewGuid():N}");

    [Fact]
    public async Task Discovery_executes_nothing_and_unsigned_plugin_can_only_use_constrained_worker()
    {
        var assembly = typeof(PluginIsolationTests).Assembly.Location;
        var package = CreatePackage("unsigned", assembly, PluginRisk.Low, signature: null);
        var worker = new RecordingWorkerLauncher();
        await using var provider = BuildServices(new FakeSignatureVerifier(false), worker);
        var discovered = await provider.GetRequiredService<IPluginCatalog>().DiscoverAsync(CancellationToken.None);
        Assert.True(discovered.IsSuccess, discovered.Failure?.Message);
        var descriptor = Assert.Single(discovered.Value);
        Assert.False(descriptor.SignatureVerified);
        Assert.Equal(PluginIsolation.OutOfProcess, descriptor.Isolation);

        var loaded = await provider.GetRequiredService<IPluginLoader>().LoadAsync(descriptor, CancellationToken.None);
        Assert.True(loaded.IsSuccess, loaded.Failure?.Message);
        await loaded.Value.DisposeAsync();
        Assert.Equal(package, descriptor.PackageDirectory);
        Assert.NotNull(worker.Request);
        Assert.False(worker.Request!.NetworkAllowed);
        Assert.Null(worker.Request.WorkspacePath);
        Assert.Equal(descriptor.Manifest.Permissions, worker.Request.Permissions);
    }

    [Fact]
    public async Task Only_verified_low_risk_plugin_is_planned_in_process_and_changed_assembly_fails_closed()
    {
        var package = CreatePackage("signed", typeof(PluginIsolationTests).Assembly.Location, PluginRisk.Low,
            new PluginSignature("test", "operator", "signature"));
        await using var provider = BuildServices(new FakeSignatureVerifier(true), new RecordingWorkerLauncher());
        var discovered = await provider.GetRequiredService<IPluginCatalog>().DiscoverAsync(CancellationToken.None);
        var descriptor = Assert.Single(discovered.Value);
        Assert.True(descriptor.SignatureVerified);
        Assert.Equal(PluginIsolation.InProcess, descriptor.Isolation);

        await File.AppendAllTextAsync(descriptor.AssemblyPath, "tamper");
        var loaded = await provider.GetRequiredService<IPluginLoader>().LoadAsync(descriptor, CancellationToken.None);
        Assert.False(loaded.IsSuccess);
        Assert.Equal(FailureCode.ConcurrencyConflict, loaded.Failure!.Code);
        Assert.Equal(package, descriptor.PackageDirectory);
    }

    [Theory]
    [InlineData(PluginRisk.Medium)]
    [InlineData(PluginRisk.High)]
    public async Task Verified_elevated_risk_plugin_remains_out_of_process(PluginRisk risk)
    {
        CreatePackage(risk.ToString(), typeof(PluginIsolationTests).Assembly.Location, risk,
            new PluginSignature("test", "operator", "signature"));
        await using var provider = BuildServices(new FakeSignatureVerifier(true), new RecordingWorkerLauncher());
        var discovered = await provider.GetRequiredService<IPluginCatalog>().DiscoverAsync(CancellationToken.None);
        Assert.Equal(PluginIsolation.OutOfProcess, Assert.Single(discovered.Value).Isolation);
    }

    [Fact]
    public async Task Production_worker_boundary_requires_container_and_denied_network()
    {
        CreatePackage("worker", typeof(PluginIsolationTests).Assembly.Location, PluginRisk.High,
            new PluginSignature("test", "operator", "signature"));
        var sandbox = new ReceiptSandbox();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentForge:Plugins:Directory"] = _root,
            ["AgentForge:Plugins:PluginWorkerExecutable"] = typeof(PluginIsolationTests).Assembly.Location,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IPluginSignatureVerifier>(new FakeSignatureVerifier(true));
        services.AddSingleton<ISandbox>(sandbox);
        services.AddAgentForgePlugins(configuration);
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var discovered = await provider.GetRequiredService<IPluginCatalog>().DiscoverAsync(CancellationToken.None);
        var loaded = await provider.GetRequiredService<IPluginLoader>()
            .LoadAsync(Assert.Single(discovered.Value), CancellationToken.None);
        Assert.True(loaded.IsSuccess, loaded.Failure?.Message);
        await loaded.Value.DisposeAsync();
        Assert.NotNull(sandbox.Request);
        Assert.Equal(ProcessSandboxKind.Container, sandbox.Request!.RequiredSandbox);
        Assert.Equal(ProcessNetworkPolicy.Denied, sandbox.Request.NetworkPolicy);
        Assert.True(sandbox.Request.RequiredFeatures.HasFlag(ProcessIsolationFeature.NetworkIsolation));
        Assert.True(sandbox.Request.RequiredFeatures.HasFlag(ProcessIsolationFeature.FileSystemIsolation));
        Assert.Empty(sandbox.Request.Environment);
    }

    [Fact]
    public async Task Configured_operator_key_verifies_only_the_exact_canonical_manifest()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
        var unsigned = new PluginManifest(
            1,
            new PluginId("operator.signed"),
            new PluginVersion("1.0.0"),
            "plugin.dll",
            "Operator.Plugin",
            "sha256:" + new string('a', 64),
            PluginRisk.Low,
            InventoryPermission,
            null);
        var signature = Convert.ToBase64String(signer.SignData(
            PluginManifestValidator.CreateSigningPayload(unsigned),
            HashAlgorithmName.SHA256));
        var signed = unsigned with
        {
            Signature = new PluginSignature("ECDSA-P256-SHA256", "operator-key", signature),
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentForge:Plugins:TrustedPublicKeys:operator-key"] = key,
        }).Build();
        var services = new ServiceCollection();
        services.AddAgentForgePlugins(configuration);
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var verifier = provider.GetRequiredService<IPluginSignatureVerifier>();

        Assert.True(await verifier.VerifyAsync(signed, "bounded-manifest"u8.ToArray(), CancellationToken.None));
        Assert.False(await verifier.VerifyAsync(
            signed with { Risk = PluginRisk.High }, "bounded-manifest"u8.ToArray(), CancellationToken.None));
        Assert.False(await verifier.VerifyAsync(
            signed with { Signature = signed.Signature! with { KeyId = "unknown-key" } },
            "bounded-manifest"u8.ToArray(), CancellationToken.None));
    }

    [Fact]
    public async Task Recovery_inspection_fails_closed_until_invalid_plugin_package_is_quarantined()
    {
        var invalidPackage = Path.Combine(_root, "invalid-package");
        Directory.CreateDirectory(invalidPackage);
        await using var provider = BuildServices(new FakeSignatureVerifier(false), new RecordingWorkerLauncher());
        var inspector = provider.GetRequiredService<IRecoveryConfigurationInspector>();

        var failed = await inspector.InspectAsync(
            new InstallationId(Guid.NewGuid()), CancellationToken.None);
        Assert.Equal(DoctorCheckStatus.Fail, failed.Status);

        Directory.Delete(invalidPackage);
        var repaired = await inspector.InspectAsync(
            new InstallationId(Guid.NewGuid()), CancellationToken.None);
        Assert.Equal(DoctorCheckStatus.Pass, repaired.Status);
    }

    private ServiceProvider BuildServices(
        IPluginSignatureVerifier verifier,
        IPluginWorkerLauncher worker)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentForge:Plugins:Directory"] = _root,
            ["AgentForge:Plugins:MaximumPackages"] = "16",
            ["AgentForge:Plugins:MaximumManifestBytes"] = "65536",
            ["AgentForge:Plugins:MaximumAssemblyBytes"] = "134217728",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(verifier);
        services.AddSingleton(worker);
        services.AddAgentForgePlugins(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private string CreatePackage(
        string suffix,
        string sourceAssembly,
        PluginRisk risk,
        PluginSignature? signature)
    {
        var package = Path.Combine(_root, $"package-{suffix.ToLowerInvariant()}");
        Directory.CreateDirectory(package);
        var assemblyPath = Path.Combine(package, "plugin.dll");
        File.Copy(sourceAssembly, assemblyPath);
        var hash = $"sha256:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))).ToLowerInvariant()}";
        File.WriteAllText(Path.Combine(package, "plugin.harness.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id = $"test.{suffix.ToLowerInvariant()}",
            version = "1.0.0",
            entryAssembly = "plugin.dll",
            entryType = "AgentForge.UnitTests.NotLoadedDuringDiscovery",
            assemblyHash = hash,
            risk = risk.ToString(),
            permissions = InventoryPermission,
            signature = signature is null ? null : new
            {
                algorithm = signature.Algorithm,
                keyId = signature.KeyId,
                value = signature.Value,
            },
        }));
        return package;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeSignatureVerifier(bool result) : IPluginSignatureVerifier
    {
        public Task<bool> VerifyAsync(
            PluginManifest manifest,
            ReadOnlyMemory<byte> manifestBytes,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class RecordingWorkerLauncher : IPluginWorkerLauncher
    {
        public PluginWorkerRequest? Request { get; private set; }

        public Task<DomainResult<IPluginHandle>> LaunchAsync(
            PluginLoadPlan plan,
            PluginWorkerRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(DomainResult.Success<IPluginHandle>(new FakePluginHandle(plan)));
        }
    }

    private sealed class FakePluginHandle(PluginLoadPlan plan) : IPluginHandle
    {
        public PluginLoadPlan Plan { get; } = plan;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ReceiptSandbox : ISandbox
    {
        public ProcessExecutionRequest? Request { get; private set; }

        public ProcessSandboxCapabilities Capabilities { get; } = new(
            ProcessSandboxKind.Container, true, (ProcessIsolationFeature)int.MaxValue, "deterministic-test");

        public Task<DomainResult<ProcessExecutionResult>> ExecuteAsync(
            ProcessExecutionRequest request,
            IProcessOutputObserver? observer,
            CancellationToken cancellationToken)
        {
            Request = request;
            var worker = JsonSerializer.Deserialize<PluginWorkerRequest>(
                Convert.FromBase64String(request.Arguments[1]))!;
            var output = JsonSerializer.SerializeToUtf8Bytes(new PluginWorkerReceipt(
                1, true, worker.PluginId, worker.PluginVersion, worker.AssemblyHash));
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(DomainResult.Success(new ProcessExecutionResult(
                0, output, [], now, now, TimeSpan.Zero, Capabilities)));
        }
    }
}
