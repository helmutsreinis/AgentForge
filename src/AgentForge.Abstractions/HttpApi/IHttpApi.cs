using AgentForge.Domain.HttpApi;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.HttpApi;

public interface IHttpApiProfileRepository
{
    ValueTask<HttpApiProfile?> FindAsync(
        InstallationId installationId,
        HttpApiProfileId profileId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<HttpApiProfile>> ListAsync(
        InstallationId installationId,
        CancellationToken cancellationToken);

    ValueTask AddAsync(HttpApiProfile profile, CancellationToken cancellationToken);

    ValueTask UpdateAsync(
        HttpApiProfile profile,
        long expectedVersion,
        CancellationToken cancellationToken);
}

public interface IHttpApiConnectivityProbe
{
    Task<DomainResult<HttpApiProbeEvidence>> ProbeAsync(
        HttpApiConfigurationCandidate candidate,
        ReadOnlyMemory<char> bearerToken,
        CancellationToken cancellationToken);
}

public interface IHttpApiConfigurationService
{
    ValueTask<HttpApiProfile?> FindAsync(
        InstallationId installationId,
        HttpApiProfileId profileId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<HttpApiProfile>> ListAsync(
        InstallationId installationId,
        CancellationToken cancellationToken);

    Task<DomainResult<HttpApiConfigurationPreview>> PreviewAsync(
        InstallationId installationId,
        long? expectedVersion,
        HttpApiConfigurationCandidate candidate,
        ReadOnlyMemory<char> bearerToken,
        ActorId actorId,
        CorrelationId correlationId,
        CancellationToken cancellationToken);

    Task<DomainResult<HttpApiConfigurationResult>> ApplyAsync(
        HttpApiConfigurationPreview preview,
        ReadOnlyMemory<char> bearerToken,
        CancellationToken cancellationToken);
}

public interface IHttpApiReadService
{
    Task<DomainResult<HttpApiReadResponse>> GetAsync(
        HttpApiProfile profile,
        HttpApiReadRequest request,
        CancellationToken cancellationToken);
}

public sealed record ResolvedHttpApiRequest(HttpApiProfile Profile, Uri Endpoint);

public interface IHttpApiRequestResolver
{
    Task<DomainResult<ResolvedHttpApiRequest>> ResolveAsync(
        InstallationId installationId,
        HttpApiProfileId profileId,
        string relativePath,
        IReadOnlyDictionary<string, string> query,
        CancellationToken cancellationToken);
}
