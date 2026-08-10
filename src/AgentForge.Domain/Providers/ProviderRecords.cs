using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;

namespace AgentForge.Domain.Providers;

public readonly record struct ProviderProfileId(Guid Value)
{
    public static ProviderProfileId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public sealed record ProviderCapabilitySummary(
    bool TextGeneration,
    bool Streaming,
    bool ToolCalls,
    bool Images,
    string EvidenceSource);

public sealed record ProviderProfile(
    ProviderProfileId Id,
    InstallationId InstallationId,
    string Name,
    string ProviderType,
    Uri Endpoint,
    string Model,
    SecretReference SecretReference,
    ProviderCapabilitySummary Capabilities,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ActorId ActorId,
    CorrelationId CorrelationId);

public sealed record ProviderProfileCandidate(
    string Name,
    string ProviderType,
    Uri Endpoint,
    string Model,
    SecretReference SecretReference);

public sealed record ConfigureProviderRequest(
    ProviderProfileCandidate Candidate,
    ActorId ActorId,
    CorrelationId CorrelationId);

public sealed record ConfigureProviderResult(ProviderProfile Profile);
