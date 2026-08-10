using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Security;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;

namespace AgentForge.Setup;

internal sealed class ConservativeProviderProfileDefinitionEvaluator(ISensitiveDataRedactor redactor)
    : IProviderProfileDefinitionEvaluator
{
    public DomainResult<ProviderProfileCandidate> NormalizeAndValidate(ProviderProfileCandidate candidate)
    {
        if (candidate is null || candidate.Endpoint is null || candidate.SecretReference is null ||
            string.IsNullOrWhiteSpace(candidate.Name) || candidate.Name.Length > 128 || candidate.Name.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(candidate.ProviderType) || candidate.ProviderType.Length > 64 || candidate.ProviderType.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(candidate.Model) || candidate.Model.Length > 256 || candidate.Model.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(candidate.SecretReference.Store) || candidate.SecretReference.Store.Length > 128 || candidate.SecretReference.Store.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(candidate.SecretReference.Key) || candidate.SecretReference.Key.Length > 512 || candidate.SecretReference.Key.Any(char.IsControl))
        {
            return Invalid("Provider profile fields are missing or invalid.");
        }

        if (!candidate.Endpoint.IsAbsoluteUri ||
            candidate.Endpoint.AbsoluteUri.Length > 2048 ||
            candidate.Endpoint.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(candidate.Endpoint.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Endpoint.Query) ||
            !string.IsNullOrEmpty(candidate.Endpoint.Fragment))
        {
            return Invalid("Provider endpoint must be an absolute HTTP or HTTPS URI without credentials, query, or fragment.");
        }

        if (redactor.Redact(new
        {
            candidate.Name,
            candidate.ProviderType,
            candidate.Model,
            Endpoint = candidate.Endpoint.AbsoluteUri,
        }).ContainsRedactions)
        {
            return Invalid("Provider profile contains credential-shaped content and cannot be persisted.");
        }

        return DomainResult.Success(candidate with
        {
            Name = candidate.Name.Trim(),
            ProviderType = candidate.ProviderType.Trim().ToLowerInvariant(),
            Model = candidate.Model.Trim(),
        });
    }

    private static DomainResult<ProviderProfileCandidate> Invalid(string message) =>
        DomainResult.Fail<ProviderProfileCandidate>(new DomainFailure(FailureCode.ValidationFailure, message));
}
