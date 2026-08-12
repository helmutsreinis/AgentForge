using AgentForge.Domain.Devices;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Devices;

public interface IDeclarativeDecoder
{
    DomainResult<DecoderParseResult> Decode(
        DeclarativeDecoderDefinition definition,
        ReadOnlyMemory<byte> input);
}

public interface IDecoderEvaluator
{
    DomainResult<DecoderEvaluationEvidence> Evaluate(
        DeclarativeDecoderDefinition definition,
        DecoderEvaluationSuite suite);
}

public interface IDecoderProposalRepository
{
    ValueTask<DecoderProposalSnapshot?> GetLatestAsync(DecoderProposalId id, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<DecoderProposalSnapshot>> ListAsync(DecoderProposalId id, CancellationToken cancellationToken);
    ValueTask<string?> GetActiveHashAsync(InstallationId installationId, string decoderId, CancellationToken cancellationToken);
    ValueTask AppendAsync(DecoderProposalSnapshot snapshot, long? expectedVersion, CancellationToken cancellationToken);
    ValueTask SetActiveHashAsync(
        InstallationId installationId, string decoderId, string? candidateHash,
        string? expectedCurrentHash, CancellationToken cancellationToken);
}

public sealed record ProposeDecoderRequest(
    DecoderProposalId Id,
    InstallationId InstallationId,
    DeclarativeDecoderDefinition Candidate,
    string? ExpectedBaselineHash,
    ActorId ProposerId,
    CorrelationId CorrelationId);

public sealed record EvaluateDecoderRequest(
    DecoderProposalId Id,
    long ExpectedVersion,
    DecoderEvaluationSuite Suite,
    ActorId EvaluatorId,
    CorrelationId CorrelationId);

public sealed record ApproveDecoderRequest(
    DecoderProposalId Id,
    long ExpectedVersion,
    ActorId ApproverId,
    CorrelationId CorrelationId);

public sealed record PromoteDecoderRequest(
    DecoderProposalId Id,
    long ExpectedVersion,
    DecoderCanaryEvidence Canary,
    ActorId GovernorId,
    CorrelationId CorrelationId);

public sealed record RollbackDecoderRequest(
    DecoderProposalId Id,
    long ExpectedVersion,
    ActorId GovernorId,
    CorrelationId CorrelationId);

public sealed record QuarantineDecoderRequest(
    DecoderProposalId Id,
    long ExpectedVersion,
    ActorId GovernorId,
    CorrelationId CorrelationId);

public interface IDecoderGovernanceService
{
    Task<DomainResult<DecoderProposalSnapshot>> ProposeAsync(ProposeDecoderRequest request, CancellationToken cancellationToken);
    Task<DomainResult<DecoderProposalSnapshot>> EvaluateAsync(EvaluateDecoderRequest request, CancellationToken cancellationToken);
    Task<DomainResult<DecoderProposalSnapshot>> ApproveAsync(ApproveDecoderRequest request, CancellationToken cancellationToken);
    Task<DomainResult<DecoderProposalSnapshot>> PromoteAsync(PromoteDecoderRequest request, CancellationToken cancellationToken);
    Task<DomainResult<DecoderProposalSnapshot>> QuarantineAsync(QuarantineDecoderRequest request, CancellationToken cancellationToken);
    Task<DomainResult<DecoderProposalSnapshot>> RollbackAsync(RollbackDecoderRequest request, CancellationToken cancellationToken);
}
