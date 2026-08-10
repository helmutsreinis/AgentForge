using System.Text;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;
using AgentForge.Security;
using AgentForge.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.SecurityTests;

public sealed class SecretStoreSecurityTests
{
    [Fact]
    public async Task Windows_store_encrypts_at_rest_and_materializes_only_a_disposable_lease()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var dataDirectory = Path.Combine(temporaryRoot, $"agentforge-secret-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            await using var services = BuildServices(dataDirectory);
            var store = services.GetRequiredService<ISecretStore>();
            Assert.True(store.GetCapability().IsAvailable);
            const string plaintext = "windows-" + "credential-value-123456";

            var stored = await store.StoreAsync("provider", plaintext.AsMemory(), CancellationToken.None);
            Assert.True(stored.IsSuccess);
            var protectedFile = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(dataDirectory, "secrets"),
                "*.dpapi"));
            var protectedBytes = await File.ReadAllBytesAsync(protectedFile);
            Assert.Equal(-1, protectedBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(plaintext)));

            var materialized = await store.MaterializeAsync(stored.Value, CancellationToken.None);
            Assert.True(materialized.IsSuccess);
            var lease = materialized.Value;
            Assert.True(lease.Value.Span.SequenceEqual(plaintext.AsSpan()));
            await lease.DisposeAsync();
            Assert.Throws<ObjectDisposedException>(() => lease.Value);

            var deleted = await store.DeleteAsync(stored.Value, CancellationToken.None);
            Assert.True(deleted.IsSuccess);
            Assert.False(File.Exists(protectedFile));
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot, dataDirectory);
        }
    }

    [Fact]
    public async Task Linux_store_reports_typed_unavailability_when_secret_tool_is_absent()
    {
        if (!OperatingSystem.IsLinux() || File.Exists("/usr/bin/secret-tool") || File.Exists("/bin/secret-tool"))
        {
            return;
        }

        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var dataDirectory = Path.Combine(temporaryRoot, $"agentforge-secret-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            await using var services = BuildServices(dataDirectory);
            var store = services.GetRequiredService<ISecretStore>();
            var capability = store.GetCapability();
            Assert.False(capability.IsAvailable);
            Assert.Equal(FailureCode.UnsupportedCapability, capability.UnavailableReason?.Code);

            var result = await store.StoreAsync(
                "provider",
                "not-a-real-secret".AsMemory(),
                CancellationToken.None);
            Assert.False(result.IsSuccess);
            Assert.Equal(FailureCode.UnsupportedCapability, result.Failure?.Code);
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot, dataDirectory);
        }
    }

    [Fact]
    public async Task Administrator_credential_keeps_only_a_fixed_time_verifier_and_os_reference()
    {
        var configuration = new ConfigurationBuilder().Build();
        var registrations = new ServiceCollection();
        registrations.AddAgentForgeSetup(configuration);
        registrations.AddSingleton<IIdentifierGenerator, SequentialIdentifierGenerator>();
        registrations.AddAgentForgeSecurity(configuration);
        registrations.AddSingleton<ISecretStore, DeterministicSecretStore>();
        await using var services = registrations.BuildServiceProvider(validateScopes: true);
        await using var scope = services.CreateAsyncScope();
        var credentials = scope.ServiceProvider.GetRequiredService<ILocalAdministratorCredentialService>();

        var created = await credentials.CreateAsync("administrator", CancellationToken.None);
        Assert.True(created.IsSuccess);
        Assert.Equal("PBKDF2-SHA256", created.Value.CredentialVerifier.Algorithm);
        Assert.True(created.Value.CredentialVerifier.WorkFactor >= 600_000);

        var materialized = await scope.ServiceProvider.GetRequiredService<ISecretStore>()
            .MaterializeAsync(created.Value.ClientCredentialReference, CancellationToken.None);
        Assert.True(materialized.IsSuccess);
        await using var lease = materialized.Value;
        Assert.True(credentials.Verify(lease.Value.Span, created.Value.CredentialVerifier));
        Assert.False(credentials.Verify("wrong-credential".AsSpan(), created.Value.CredentialVerifier));
        Assert.DoesNotContain(
            new string(lease.Value.Span),
            created.Value.CredentialVerifier.Verifier,
            StringComparison.Ordinal);
    }

    private static ServiceProvider BuildServices(string dataDirectory)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentForge:Installation:DataDirectory"] = dataDirectory,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddAgentForgeSetup(configuration);
        services.AddSingleton<IIdentifierGenerator, SequentialIdentifierGenerator>();
        services.AddAgentForgeSecurity(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static void DeleteTemporaryDirectory(string temporaryRoot, string dataDirectory)
    {
        var verifiedPath = Path.GetFullPath(dataDirectory);
        if (Directory.Exists(verifiedPath) &&
            verifiedPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(verifiedPath).StartsWith("agentforge-secret-test-", StringComparison.Ordinal))
        {
            Directory.Delete(verifiedPath, recursive: true);
        }
    }

    private sealed class SequentialIdentifierGenerator : IIdentifierGenerator
    {
        private int _value;

        public Guid NewGuid()
        {
            var value = Interlocked.Increment(ref _value);
            return Guid.Parse($"00000000-0000-0000-0000-{value:000000000000}");
        }
    }
}
