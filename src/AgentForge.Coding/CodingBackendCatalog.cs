using AgentForge.Abstractions.Coding;
using AgentForge.Domain.Coding;
using AgentForge.Domain.Primitives;

namespace AgentForge.Coding;

internal sealed class CodingBackendCatalog(IEnumerable<ICodingBackend> backends) : ICodingBackendCatalog
{
    private readonly Dictionary<(string Id, string Version), ICodingBackend> _backends = Build(backends);

    public ValueTask<DomainResult<CodingBackendDescriptor>> DescribeAsync(
        string backendId,
        string version,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_backends.TryGetValue((backendId, version), out var backend)
            ? DomainResult.Success(backend.Descriptor with { Languages = backend.Descriptor.Languages.ToArray() })
            : Missing<CodingBackendDescriptor>());
    }

    public ValueTask<DomainResult<ICodingBackend>> ResolveAsync(
        string backendId,
        string version,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_backends.TryGetValue((backendId, version), out var backend)
            ? DomainResult.Success(backend)
            : Missing<ICodingBackend>());
    }

    private static Dictionary<(string Id, string Version), ICodingBackend> Build(IEnumerable<ICodingBackend> backends)
    {
        ArgumentNullException.ThrowIfNull(backends);
        var result = new Dictionary<(string, string), ICodingBackend>();
        foreach (var backend in backends)
        {
            var descriptor = backend.Descriptor;
            if (string.IsNullOrWhiteSpace(descriptor.Id) || descriptor.Id.Length > 256 ||
                !Domain.Skills.SkillVersion.TryParse(descriptor.Version, out _) ||
                descriptor.Languages is null || descriptor.Languages.Count is < 1 or > 32 ||
                descriptor.Languages.Any(string.IsNullOrWhiteSpace) ||
                !descriptor.SupportsPatchProposal || !result.TryAdd((descriptor.Id, descriptor.Version), backend))
            {
                throw new InvalidOperationException("Coding backend descriptors must be unique, bounded, versioned, and patch-only capable.");
            }
        }

        return result;
    }

    private static DomainResult<T> Missing<T>() => DomainResult.Fail<T>(new DomainFailure(
        FailureCode.UnsupportedCapability, "The exact coding backend is unavailable."));
}
