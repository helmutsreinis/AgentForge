using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Security;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;

namespace AgentForge.Setup;

internal sealed class DeterministicProviderProfileValidator(ISecretStore secretStore) : IProviderProfileValidator
{
    public async Task<DomainResult<ProviderCapabilitySummary>> ValidateAsync(
        ProviderProfileCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!string.Equals(candidate.ProviderType, "deterministic", StringComparison.OrdinalIgnoreCase))
        {
            return DomainResult.Fail<ProviderCapabilitySummary>(new DomainFailure(
                FailureCode.UnsupportedCapability,
                "This setup slice supports deterministic provider validation only."));
        }

        if (!string.Equals(candidate.SecretReference.Store, secretStore.StoreName, StringComparison.Ordinal))
        {
            return DomainResult.Fail<ProviderCapabilitySummary>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Provider secret reference does not match the configured OS secret store."));
        }

        var materialized = await secretStore.MaterializeAsync(candidate.SecretReference, cancellationToken);
        if (!materialized.IsSuccess)
        {
            return DomainResult.Fail<ProviderCapabilitySummary>(materialized.Failure!);
        }

        await using var lease = materialized.Value;
        if (lease.Value.IsEmpty)
        {
            return DomainResult.Fail<ProviderCapabilitySummary>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Provider credential is empty."));
        }

        return DomainResult.Success(new ProviderCapabilitySummary(
            TextGeneration: true,
            Streaming: true,
            ToolCalls: true,
            Images: false,
            EvidenceSource: "deterministic-validation-v1"));
    }
}
