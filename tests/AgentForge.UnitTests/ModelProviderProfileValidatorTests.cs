using AgentForge.Abstractions.Security;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Security;
using AgentForge.Models;

namespace AgentForge.UnitTests;

public sealed class ModelProviderProfileValidatorTests
{
    [Theory]
    [InlineData("openai", "https://api.openai.example/v1/chat/completions")]
    [InlineData("deepseek", "https://api.deepseek.example/v1/chat/completions")]
    [InlineData("vllm", "http://192.168.1.89:8000/v1/chat/completions")]
    [InlineData("openai-compatible", "http://127.0.0.1:8000/v1/chat/completions")]
    [InlineData("anthropic", "https://api.anthropic.example/v1/messages")]
    public async Task Named_compatible_profiles_validate_secret_reference_and_safe_transport(
        string providerType,
        string endpoint)
    {
        var store = new StubSecretStore("bounded-credential");
        var validator = new ModelProviderProfileValidator(store);

        var result = await validator.ValidateAsync(
            Candidate(providerType, endpoint, store),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.True(result.Value.TextGeneration);
        Assert.True(result.Value.Streaming);
        Assert.False(result.Value.ToolCalls);
        Assert.Equal($"configured-unprobed-{providerType}-v1", result.Value.EvidenceSource);
        Assert.Equal(1, store.MaterializeCalls);
        Assert.NotNull(store.LastBuffer);
        Assert.All(store.LastBuffer!, character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task Cloud_plaintext_unsupported_type_store_mismatch_and_header_injection_fail_closed()
    {
        var store = new StubSecretStore("bounded-credential");
        var validator = new ModelProviderProfileValidator(store);

        var plaintext = await validator.ValidateAsync(
            Candidate("openai", "http://192.168.1.89:8000/v1/chat/completions", store),
            CancellationToken.None);
        Assert.False(plaintext.IsSuccess);

        var unsupported = await validator.ValidateAsync(
            Candidate("unknown-provider", "https://provider.example/v1", store),
            CancellationToken.None);
        Assert.False(unsupported.IsSuccess);
        Assert.Equal(FailureCode.UnsupportedCapability, unsupported.Failure?.Code);

        var mismatch = await validator.ValidateAsync(
            Candidate("deepseek", "https://provider.example/v1", store) with
            {
                SecretReference = new SecretReference("other-store", "profile-secret"),
            },
            CancellationToken.None);
        Assert.False(mismatch.IsSuccess);

        var injectedStore = new StubSecretStore("token\r\nX-Injected: true");
        var injected = await new ModelProviderProfileValidator(injectedStore).ValidateAsync(
            Candidate("openai", "https://provider.example/v1", injectedStore),
            CancellationToken.None);
        Assert.False(injected.IsSuccess);
        Assert.NotNull(injectedStore.LastBuffer);
        Assert.All(injectedStore.LastBuffer!, character => Assert.Equal('\0', character));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("deepseek")]
    [InlineData("vllm")]
    [InlineData("openai-compatible")]
    public void Compatible_wire_adapter_preserves_named_provider_identity(string providerType)
    {
        var descriptor = new AgentForge.Domain.Models.ModelProviderDescriptor(
            new ProviderProfileId(Guid.NewGuid()),
            providerType,
            "fixture-model",
            [
                Evidence(AgentForge.Domain.Models.ModelCapability.TextGeneration),
                Evidence(AgentForge.Domain.Models.ModelCapability.Streaming),
            ]);

        using var provider = OpenAiCompatibleModelProvider.Create(
            descriptor,
            new OpenAiCompatibleModelProviderOptions(
                new Uri("https://provider.example/v1/chat/completions")),
            new PassthroughContextPreparer(),
            new FixedClock()).Value;

        Assert.Equal(providerType, provider.Descriptor.ProviderType);
    }

    private static ProviderProfileCandidate Candidate(
        string providerType,
        string endpoint,
        ISecretStore store) => new(
        "named-profile",
        providerType,
        new Uri(endpoint),
        "fixture-model",
        new SecretReference(store.StoreName, "profile-secret"));

    private static AgentForge.Domain.Models.ModelCapabilityEvidence Evidence(
        AgentForge.Domain.Models.ModelCapability capability) => new(
        capability,
        AgentForge.Domain.Models.ModelCapabilityEvidenceSource.Declared,
        AgentForge.Domain.Models.ModelCapabilityAvailability.Available,
        "fixture",
        DateTimeOffset.UtcNow);

    private sealed class StubSecretStore(string value) : ISecretStore
    {
        public string StoreName => "stub-store";

        public int MaterializeCalls { get; private set; }

        public char[]? LastBuffer { get; private set; }

        public SecretStoreCapability GetCapability() => new(StoreName, true, null);

        public Task<DomainResult<SecretReference>> StoreAsync(
            string logicalName,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DomainResult<SecretLease>> MaterializeAsync(
            SecretReference secretReference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MaterializeCalls++;
            LastBuffer = value.ToCharArray();
            return Task.FromResult(DomainResult.Success(new SecretLease(LastBuffer)));
        }

        public Task<DomainResult<bool>> DeleteAsync(
            SecretReference secretReference,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class PassthroughContextPreparer : AgentForge.Abstractions.Models.IModelContextPreparer
    {
        public DomainResult<AgentForge.Domain.Models.PreparedModelContext> Prepare(
            AgentForge.Domain.Models.ModelRequest request) =>
            DomainResult.Success(new AgentForge.Domain.Models.PreparedModelContext(
                request,
                0,
                ModelContextPreparer.PolicyName,
                "sha256:" + new string('a', 64)));
    }

    private sealed class FixedClock : AgentForge.Abstractions.Time.IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }
}
