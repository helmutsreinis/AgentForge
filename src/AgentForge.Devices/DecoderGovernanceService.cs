using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Devices;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Devices;
using AgentForge.Domain.Primitives;

namespace AgentForge.Devices;

internal sealed class DecoderGovernanceService(
    IDecoderProposalRepository repository,
    IDecoderEvaluator evaluator,
    IAuditRecorder audit,
    IUnitOfWork unitOfWork,
    IClock clock) : IDecoderGovernanceService
{
    public async Task<DomainResult<DecoderProposalSnapshot>> ProposeAsync(
        ProposeDecoderRequest request, CancellationToken cancellationToken)
    {
        if (request is null || request.Id.Value == Guid.Empty || request.InstallationId.Value == Guid.Empty ||
            request.Candidate is null || !request.Candidate.IsValid() ||
            !Text(request.ProposerId.Value, 256) || !Text(request.CorrelationId.Value, 128) ||
            request.ExpectedBaselineHash is not null && !SerialDeviceRecordValidator.IsSha256(request.ExpectedBaselineHash))
            return Invalid("Decoder proposal identity, candidate, permissions, or baseline is invalid.");
        var existing = await repository.GetLatestAsync(request.Id, cancellationToken);
        if (existing is not null)
            return existing.Candidate.DefinitionHash == request.Candidate.DefinitionHash &&
                existing.BaselineHash == request.ExpectedBaselineHash && existing.ProposerId == request.ProposerId
                ? DomainResult.Success(existing) : Conflict("Decoder proposal ID is bound to different input.");
        var active = await repository.GetActiveHashAsync(request.InstallationId, request.Candidate.DecoderId, cancellationToken);
        if (active != request.ExpectedBaselineHash) return Conflict("Decoder proposal baseline is stale.");
        DecoderProposalSnapshot snapshot;
        try
        {
            snapshot = DecoderProposalStateMachine.Propose(request.Id, request.InstallationId, request.Candidate,
                request.ExpectedBaselineHash, request.ProposerId, clock.UtcNow);
        }
        catch (InvalidOperationException exception) { return Invalid(exception.Message); }
        await repository.AppendAsync(snapshot, null, cancellationToken);
        return await CommitAsync(snapshot, request.ProposerId, request.CorrelationId,
            "decoder.proposed", new { snapshot.Candidate.DecoderId, snapshot.Candidate.DefinitionHash, snapshot.BaselineHash }, cancellationToken);
    }

    public async Task<DomainResult<DecoderProposalSnapshot>> EvaluateAsync(
        EvaluateDecoderRequest request, CancellationToken cancellationToken)
    {
        var current = await CurrentAsync(request?.Id ?? default, request?.ExpectedVersion ?? -1, cancellationToken);
        if (!current.IsSuccess) return current;
        if (request is null || !Text(request.EvaluatorId.Value, 256) || !Text(request.CorrelationId.Value, 128) ||
            request.EvaluatorId == current.Value.ProposerId)
            return Denied("Decoder evaluation requires a distinct evaluator identity.");
        if (current.Value.State != DecoderProposalState.Proposed)
            return current.Value.Evaluation?.SuiteHash == request.Suite.SuiteHash
                ? DomainResult.Success(current.Value) : Conflict("Decoder proposal is not awaiting this evaluation.");
        var evidence = evaluator.Evaluate(current.Value.Candidate, request.Suite);
        if (!evidence.IsSuccess) return DomainResult.Fail<DecoderProposalSnapshot>(evidence.Failure!);
        var next = DecoderProposalStateMachine.Evaluate(current.Value, evidence.Value, clock.UtcNow);
        await repository.AppendAsync(next, current.Value.Version, cancellationToken);
        return await CommitAsync(next, request.EvaluatorId, request.CorrelationId, "decoder.evaluated",
            new { evidence.Value.Passed, evidence.Value.EvidenceHash, evidence.Value.SuiteHash }, cancellationToken);
    }

    public async Task<DomainResult<DecoderProposalSnapshot>> ApproveAsync(
        ApproveDecoderRequest request, CancellationToken cancellationToken)
    {
        var current = await CurrentAsync(request?.Id ?? default, request?.ExpectedVersion ?? -1, cancellationToken);
        if (!current.IsSuccess) return current;
        if (request is null || !Text(request.ApproverId.Value, 256) || !Text(request.CorrelationId.Value, 128))
            return Invalid("Decoder approval identity is invalid.");
        DecoderProposalSnapshot next;
        try { next = DecoderProposalStateMachine.Approve(current.Value, request.ApproverId, clock.UtcNow); }
        catch (InvalidOperationException exception) { return Denied(exception.Message); }
        await repository.AppendAsync(next, current.Value.Version, cancellationToken);
        return await CommitAsync(next, request.ApproverId, request.CorrelationId, "decoder.approved",
            new { next.Candidate.DefinitionHash, Approver = request.ApproverId.Value }, cancellationToken);
    }

    public async Task<DomainResult<DecoderProposalSnapshot>> PromoteAsync(
        PromoteDecoderRequest request, CancellationToken cancellationToken)
    {
        var current = await CurrentAsync(request?.Id ?? default, request?.ExpectedVersion ?? -1, cancellationToken);
        if (!current.IsSuccess) return current;
        if (request is null || !Text(request.GovernorId.Value, 256) || !Text(request.CorrelationId.Value, 128) ||
            request.GovernorId == current.Value.ProposerId)
            return Denied("Decoder promotion requires a distinct governor identity.");
        var active = await repository.GetActiveHashAsync(current.Value.InstallationId, current.Value.Candidate.DecoderId, cancellationToken);
        if (active != current.Value.BaselineHash) return Conflict("Decoder promotion baseline is stale.");
        DecoderProposalSnapshot next;
        try { next = DecoderProposalStateMachine.RecordCanary(current.Value, request.Canary, clock.UtcNow); }
        catch (InvalidOperationException exception) { return Denied(exception.Message); }
        if (next.State == DecoderProposalState.Active)
            await repository.SetActiveHashAsync(next.InstallationId, next.Candidate.DecoderId,
                next.Candidate.DefinitionHash, next.BaselineHash, cancellationToken);
        await repository.AppendAsync(next, current.Value.Version, cancellationToken);
        return await CommitAsync(next, request.GovernorId, request.CorrelationId,
            next.State == DecoderProposalState.Active ? "decoder.promoted" : "decoder.quarantined",
            new { next.Candidate.DefinitionHash, request.Canary.EvidenceHash }, cancellationToken);
    }

    public async Task<DomainResult<DecoderProposalSnapshot>> QuarantineAsync(
        QuarantineDecoderRequest request, CancellationToken cancellationToken)
    {
        var current = await CurrentAsync(request?.Id ?? default, request?.ExpectedVersion ?? -1, cancellationToken);
        if (!current.IsSuccess) return current;
        if (request is null || !Text(request.GovernorId.Value, 256) || !Text(request.CorrelationId.Value, 128) ||
            request.GovernorId == current.Value.ProposerId)
            return Denied("Decoder quarantine requires a distinct governor identity.");
        DecoderProposalSnapshot next;
        try { next = DecoderProposalStateMachine.Quarantine(current.Value, clock.UtcNow); }
        catch (InvalidOperationException exception) { return Denied(exception.Message); }
        await repository.AppendAsync(next, current.Value.Version, cancellationToken);
        return await CommitAsync(next, request.GovernorId, request.CorrelationId, "decoder.quarantined",
            new { next.Candidate.DefinitionHash }, cancellationToken);
    }

    public async Task<DomainResult<DecoderProposalSnapshot>> RollbackAsync(
        RollbackDecoderRequest request, CancellationToken cancellationToken)
    {
        var current = await CurrentAsync(request?.Id ?? default, request?.ExpectedVersion ?? -1, cancellationToken);
        if (!current.IsSuccess) return current;
        if (request is null || !Text(request.GovernorId.Value, 256) || !Text(request.CorrelationId.Value, 128) ||
            request.GovernorId == current.Value.ProposerId)
            return Denied("Decoder rollback requires a distinct governor identity.");
        var active = await repository.GetActiveHashAsync(current.Value.InstallationId, current.Value.Candidate.DecoderId, cancellationToken);
        if (active != current.Value.Candidate.DefinitionHash) return Conflict("Decoder is no longer the active version.");
        DecoderProposalSnapshot next;
        try { next = DecoderProposalStateMachine.Rollback(current.Value, clock.UtcNow); }
        catch (InvalidOperationException exception) { return Denied(exception.Message); }
        await repository.SetActiveHashAsync(next.InstallationId, next.Candidate.DecoderId,
            next.BaselineHash, next.Candidate.DefinitionHash, cancellationToken);
        await repository.AppendAsync(next, current.Value.Version, cancellationToken);
        return await CommitAsync(next, request.GovernorId, request.CorrelationId, "decoder.rolled_back",
            new { RemovedHash = next.Candidate.DefinitionHash, RestoredHash = next.BaselineHash }, cancellationToken);
    }

    private async Task<DomainResult<DecoderProposalSnapshot>> CurrentAsync(
        DecoderProposalId id, long expectedVersion, CancellationToken cancellationToken)
    {
        if (id.Value == Guid.Empty || expectedVersion < 0) return Invalid("Decoder proposal identity or version is invalid.");
        var current = await repository.GetLatestAsync(id, cancellationToken);
        if (current is null) return DomainResult.Fail<DecoderProposalSnapshot>(new(FailureCode.ValidationFailure, "Decoder proposal was not found."));
        return current.Version == expectedVersion ? DomainResult.Success(current) : Conflict("Decoder proposal version is stale.");
    }

    private async Task<DomainResult<DecoderProposalSnapshot>> CommitAsync(
        DecoderProposalSnapshot snapshot, ActorId actor, CorrelationId correlation,
        string operation, object output, CancellationToken cancellationToken)
    {
        await audit.RecordAsync(new AuditRecordRequest(snapshot.InstallationId, actor, correlation, null, operation,
            AuditOutcome.Succeeded, new { ProposalId = snapshot.Id.ToString(), snapshot.Version, snapshot.State }, output, null), cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded ? DomainResult.Success(snapshot) : DomainResult.Fail<DecoderProposalSnapshot>(commit.Failure!);
    }

    private static bool Text(string value, int maximum) => SerialDeviceRecordValidator.Text(value, maximum);
    private static DomainResult<DecoderProposalSnapshot> Invalid(string message) => DomainResult.Fail<DecoderProposalSnapshot>(new(FailureCode.ValidationFailure, message));
    private static DomainResult<DecoderProposalSnapshot> Conflict(string message) => DomainResult.Fail<DecoderProposalSnapshot>(new(FailureCode.ConcurrencyConflict, message));
    private static DomainResult<DecoderProposalSnapshot> Denied(string message) => DomainResult.Fail<DecoderProposalSnapshot>(new(FailureCode.PolicyDenied, message));
}
