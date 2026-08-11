using System.Collections.ObjectModel;
using AgentForge.Abstractions.Models;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;

namespace AgentForge.Models;

public sealed class ModelProviderCatalog : IModelProviderCatalog
{
    private readonly IReadOnlyDictionary<ProviderProfileId, IModelProvider> _providers;
    private readonly IReadOnlyList<ModelProviderDescriptor> _descriptors;

    private ModelProviderCatalog(
        IReadOnlyDictionary<ProviderProfileId, IModelProvider> providers,
        IReadOnlyList<ModelProviderDescriptor> descriptors)
    {
        _providers = providers;
        _descriptors = descriptors;
    }

    public static DomainResult<ModelProviderCatalog> Create(IEnumerable<IModelProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var indexed = new Dictionary<ProviderProfileId, IModelProvider>();
        var descriptors = new List<ModelProviderDescriptor>();
        foreach (var provider in providers)
        {
            if (provider is null)
            {
                return Invalid("Model provider catalog cannot contain a null adapter.");
            }

            var normalized = ModelContractValidator.NormalizeDescriptor(provider.Descriptor);
            if (!normalized.IsSuccess)
            {
                return DomainResult.Fail<ModelProviderCatalog>(normalized.Failure!);
            }

            var snapshot = new CatalogProviderSnapshot(provider, normalized.Value);
            if (!indexed.TryAdd(normalized.Value.ProfileId, snapshot))
            {
                return Invalid("Model provider catalog contains a duplicate profile ID.");
            }

            descriptors.Add(normalized.Value);
        }

        var ordered = descriptors
            .OrderBy(item => item.ProviderType, StringComparer.Ordinal)
            .ThenBy(item => item.Model, StringComparer.Ordinal)
            .ThenBy(item => item.ProfileId.Value)
            .ToArray();
        return DomainResult.Success(new ModelProviderCatalog(
            new ReadOnlyDictionary<ProviderProfileId, IModelProvider>(indexed),
            Array.AsReadOnly(ordered)));
    }

    public DomainResult<IModelProvider> Resolve(ProviderProfileId profileId)
    {
        if (profileId.Value == Guid.Empty)
        {
            return DomainResult.Fail<IModelProvider>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Model provider resolution requires a non-empty profile ID."));
        }

        return _providers.TryGetValue(profileId, out var provider)
            ? DomainResult.Success(provider)
            : DomainResult.Fail<IModelProvider>(new DomainFailure(
                FailureCode.UnsupportedCapability,
                "The exact model provider profile is not available."));
    }

    public IReadOnlyList<ModelProviderDescriptor> List() => _descriptors;

    private static DomainResult<ModelProviderCatalog> Invalid(string message) =>
        DomainResult.Fail<ModelProviderCatalog>(new DomainFailure(FailureCode.ValidationFailure, message));

    private sealed class CatalogProviderSnapshot(
        IModelProvider inner,
        ModelProviderDescriptor descriptor) : IModelProvider
    {
        public ModelProviderDescriptor Descriptor { get; } = descriptor;

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            CancellationToken cancellationToken) => inner.StreamAsync(request, cancellationToken);
    }
}
