using AgentForge.Domain.Coding;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Coding;

public sealed record CreateCodingSessionRequest(
    CodingSessionId SessionId,
    CodingWorkspace Workspace,
    CodingAuthoritySnapshot Authority,
    string RepositoryProfileHash,
    string Objective,
    string BackendId,
    string BackendVersion,
    IReadOnlyList<string> InstructionHashes,
    CodingPlan Plan,
    CodingVerificationPlan VerificationPlan,
    ActorId ActorId,
    string IdempotencyKey,
    CorrelationId CorrelationId,
    CorrelationId? CausationId);

public interface ICodingSessionRepository
{
    ValueTask AppendAsync(CodingSessionSnapshot snapshot, CancellationToken cancellationToken);

    ValueTask<CodingSessionSnapshot?> FindLatestAsync(
        CodingSessionId sessionId,
        CancellationToken cancellationToken);

    ValueTask<CodingSessionSnapshot?> FindByIdempotencyKeyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

public interface ICodingReviewer
{
    Task<DomainResult<CodingReviewReport>> ReviewAsync(
        CodingWorkspace workspace,
        CodingPatchReceipt patch,
        CodingVerificationReceipt verification,
        CancellationToken cancellationToken);
}

public interface ICodingSessionService
{
    Task<DomainResult<CodingSessionSnapshot>> CreateAsync(
        CreateCodingSessionRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<CodingSessionSnapshot>> ProposeAsync(
        CodingSessionId sessionId,
        long expectedVersion,
        string objective,
        CancellationToken cancellationToken);

    Task<DomainResult<CodingSessionSnapshot>> ApplyAsync(
        CodingSessionId sessionId,
        long expectedVersion,
        CancellationToken cancellationToken);

    Task<DomainResult<CodingSessionSnapshot>> VerifyAsync(
        CodingSessionId sessionId,
        long expectedVersion,
        CancellationToken cancellationToken);

    Task<DomainResult<CodingSessionSnapshot>> ReviewAsync(
        CodingSessionId sessionId,
        long expectedVersion,
        CancellationToken cancellationToken);

    Task<DomainResult<CodingSessionSnapshot>> CompleteAsync(
        CodingSessionId sessionId,
        long expectedVersion,
        CancellationToken cancellationToken);

    Task<DomainResult<CodingSessionSnapshot>> ResumeAsync(
        CodingSessionId sessionId,
        CancellationToken cancellationToken);
}
