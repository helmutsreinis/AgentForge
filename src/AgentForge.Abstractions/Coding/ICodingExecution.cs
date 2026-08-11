using AgentForge.Domain.Coding;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Coding;

public interface ICodingPatchApplier
{
    Task<DomainResult<CodingPatchReceipt>> ApplyAsync(
        CodingWorkspace workspace,
        CodingPatchSet patch,
        CancellationToken cancellationToken);
}

public interface ICodingVerifier
{
    Task<DomainResult<CodingVerificationReceipt>> VerifyAsync(
        CodingWorkspace workspace,
        CodingAuthoritySnapshot authority,
        CodingVerificationPlan plan,
        CancellationToken cancellationToken);
}

public interface ICodingBackend
{
    CodingBackendDescriptor Descriptor { get; }

    Task<DomainResult<CodingBackendProposal>> ProposeAsync(
        CodingBackendRequest request,
        CancellationToken cancellationToken);
}

public interface ILanguageServerAdapter
{
    string Language { get; }

    Task<DomainResult<SemanticResult>> NavigateAsync(
        RepositoryProfile repository,
        SemanticQuery query,
        CancellationToken cancellationToken);
}

public interface ICodingBackendCatalog
{
    ValueTask<DomainResult<CodingBackendDescriptor>> DescribeAsync(
        string backendId,
        string version,
        CancellationToken cancellationToken);

    ValueTask<DomainResult<ICodingBackend>> ResolveAsync(
        string backendId,
        string version,
        CancellationToken cancellationToken);
}
