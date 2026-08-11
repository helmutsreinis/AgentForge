using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Security;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;

namespace AgentForge.Models;

internal sealed class ModelProviderProfileValidator(ISecretStore secretStore) : IProviderProfileValidator
{
    private static readonly HashSet<string> CompatibleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "openai",
        "deepseek",
        "vllm",
        "openai-compatible",
    };

    public async Task<DomainResult<ProviderCapabilitySummary>> ValidateAsync(
        ProviderProfileCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!string.Equals(candidate.SecretReference.Store, secretStore.StoreName, StringComparison.Ordinal))
        {
            return Invalid("Provider secret reference does not match the configured OS secret store.");
        }

        var providerType = candidate.ProviderType.Trim().ToLowerInvariant();
        if (!string.Equals(providerType, "deterministic", StringComparison.Ordinal) &&
            !CompatibleTypes.Contains(providerType))
        {
            return DomainResult.Fail<ProviderCapabilitySummary>(new DomainFailure(
                FailureCode.UnsupportedCapability,
                "The requested provider type has no installed AgentForge adapter."));
        }

        if (!IsSafeEndpoint(candidate.Endpoint, providerType))
        {
            return Invalid("The provider endpoint does not satisfy its transport and destination policy.");
        }

        var materialized = await secretStore.MaterializeAsync(candidate.SecretReference, cancellationToken);
        if (!materialized.IsSuccess)
        {
            return DomainResult.Fail<ProviderCapabilitySummary>(materialized.Failure!);
        }

        await using var lease = materialized.Value;
        if (!IsSafeCredential(lease.Value.Span))
        {
            return Invalid("The provider credential is empty or cannot use the bounded header transport.");
        }

        var deterministic = string.Equals(providerType, "deterministic", StringComparison.Ordinal);
        return DomainResult.Success(new ProviderCapabilitySummary(
            TextGeneration: true,
            Streaming: true,
            ToolCalls: deterministic,
            Images: false,
            EvidenceSource: deterministic
                ? "deterministic-validation-v1"
                : $"configured-unprobed-{providerType}-v1"));
    }

    private static bool IsSafeEndpoint(Uri endpoint, string providerType)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.AbsoluteUri.Length > 2048 ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) || endpoint.Scheme is not ("http" or "https"))
        {
            return false;
        }

        if (string.Equals(providerType, "deterministic", StringComparison.Ordinal))
        {
            return EndpointDestinationPolicy.Infer(endpoint) is ModelProviderDataLocation.Loopback;
        }

        if (endpoint.Scheme == "https")
        {
            return true;
        }

        return providerType is "vllm" or "openai-compatible" &&
            EndpointDestinationPolicy.Infer(endpoint) is
                ModelProviderDataLocation.Loopback or ModelProviderDataLocation.PrivateNetwork;
    }

    private static bool IsSafeCredential(ReadOnlySpan<char> credential)
    {
        if (credential.Length is < 1 or > 8192)
        {
            return false;
        }

        foreach (var character in credential)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }

    private static DomainResult<ProviderCapabilitySummary> Invalid(string message) =>
        DomainResult.Fail<ProviderCapabilitySummary>(new DomainFailure(FailureCode.ValidationFailure, message));
}
