using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Coding;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Coding;
using AgentForge.Domain.Primitives;

namespace AgentForge.Coding;

internal sealed class CodingSessionService(
    ICodingSessionRepository repository,
    ICodingBackendCatalog backends,
    ICodingPatchApplier patcher,
    ICodingVerifier verifier,
    ICodingReviewer reviewer,
    IArtifactStore artifacts,
    IAuditRecorder audit,
    IUnitOfWork unitOfWork,
    IClock clock) : ICodingSessionService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<DomainResult<CodingSessionSnapshot>> CreateAsync(
        CreateCodingSessionRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Objective) || request.Objective.Length > 16_384 ||
            request.Objective.Any(char.IsControl))
        {
            return Invalid("The coding objective is invalid or exceeds its bound.");
        }

        var existing = await repository.FindByIdempotencyKeyAsync(
            request.Authority.InstallationId, request.IdempotencyKey, cancellationToken);
        var objectiveHash = CodingPatchValidator.Hash(request.Objective);
        if (existing is not null)
        {
            return existing.Id == request.SessionId && existing.ObjectiveHash == objectiveHash &&
                existing.Authority == request.Authority && existing.Workspace == request.Workspace
                ? DomainResult.Success(existing)
                : Conflict("The coding idempotency key is bound to a different request.");
        }

        var backend = await backends.ResolveAsync(request.BackendId, request.BackendVersion, cancellationToken);
        if (!backend.IsSuccess)
        {
            return DomainResult.Fail<CodingSessionSnapshot>(backend.Failure!);
        }

        var created = CodingSessionStateMachine.Create(
            request.SessionId, request.Workspace, request.Authority, request.RepositoryProfileHash,
            objectiveHash, request.BackendId, request.BackendVersion, request.InstructionHashes,
            request.Plan, request.VerificationPlan, request.ActorId, request.IdempotencyKey,
            request.CorrelationId, request.CausationId, clock.UtcNow);
        return created.IsSuccess
            ? await AppendAndCommitAsync(created.Value, "coding.session-created", cancellationToken)
            : created;
    }

    public async Task<DomainResult<CodingSessionSnapshot>> ProposeAsync(
        CodingSessionId sessionId,
        long expectedVersion,
        string objective,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(sessionId, expectedVersion, CodingSessionState.Prepared, cancellationToken);
        if (!loaded.IsSuccess) return loaded;
        var current = loaded.Value;
        if (string.IsNullOrWhiteSpace(objective) || objective.Length > 16_384 || objective.Any(char.IsControl) ||
            CodingPatchValidator.Hash(objective) != current.ObjectiveHash)
        {
            return Conflict("The coding objective does not match the durable session identity.");
        }

        var resolved = await backends.ResolveAsync(current.BackendId, current.BackendVersion, cancellationToken);
        if (!resolved.IsSuccess) return DomainResult.Fail<CodingSessionSnapshot>(resolved.Failure!);
        var proposal = await resolved.Value.ProposeAsync(new CodingBackendRequest(
            current.Id, current.Workspace.BaselineCommit, current.Workspace.BaselineTreeHash,
            current.Authority, current.RepositoryProfileHash, objective, current.Plan.PlanHash,
            current.InstructionHashes), cancellationToken);
        if (!proposal.IsSuccess) return DomainResult.Fail<CodingSessionSnapshot>(proposal.Failure!);
        var canonical = CodingPatchValidator.CreateBackendProposal(
            current.BackendId, current.BackendVersion, proposal.Value.Patch);
        if (!canonical.IsSuccess || proposal.Value != canonical.Value ||
            proposal.Value.Patch.BaselineTreeHash != current.Workspace.BaselineTreeHash)
        {
            return Invalid("The coding backend returned substituted or non-canonical patch evidence.");
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(proposal.Value.Patch, SerializerOptions);
        await using var content = new MemoryStream(bytes, writable: false);
        var artifact = await artifacts.PutAsync(
            content, "application/vnd.agentforge.coding-patch+json", cancellationToken);
        var next = CodingSessionStateMachine.RecordProposal(current, proposal.Value.Patch.PatchHash, artifact, clock.UtcNow);
        return next.IsSuccess
            ? await AppendAndCommitAsync(next.Value, "coding.patch-proposed", cancellationToken)
            : next;
    }

    public async Task<DomainResult<CodingSessionSnapshot>> ApplyAsync(
        CodingSessionId sessionId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(sessionId, expectedVersion, CodingSessionState.PatchProposed, cancellationToken);
        if (!loaded.IsSuccess) return loaded;
        var current = loaded.Value;
        var patch = await OpenPatchAsync(current, cancellationToken);
        if (!patch.IsSuccess) return DomainResult.Fail<CodingSessionSnapshot>(patch.Failure!);
        var receipt = await patcher.ApplyAsync(current.Workspace, patch.Value, cancellationToken);
        if (!receipt.IsSuccess) return DomainResult.Fail<CodingSessionSnapshot>(receipt.Failure!);
        var next = CodingSessionStateMachine.RecordPatch(current, receipt.Value, clock.UtcNow);
        return next.IsSuccess
            ? await AppendAndCommitAsync(next.Value, "coding.patch-applied", cancellationToken)
            : next;
    }

    public async Task<DomainResult<CodingSessionSnapshot>> VerifyAsync(
        CodingSessionId sessionId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(sessionId, expectedVersion, null, cancellationToken);
        if (!loaded.IsSuccess) return loaded;
        var current = loaded.Value;
        if (current.State is CodingSessionState.Patched)
        {
            var started = CodingSessionStateMachine.StartVerification(current, clock.UtcNow);
            if (!started.IsSuccess) return started;
            var committed = await AppendAndCommitAsync(started.Value, "coding.verification-started", cancellationToken);
            if (!committed.IsSuccess) return committed;
            current = committed.Value;
        }
        else if (current.State is not CodingSessionState.Verifying)
        {
            return Invalid("Only a patched or interrupted-verifying session can verify.");
        }

        while (current.State is CodingSessionState.Verifying)
        {
            var command = current.VerificationPlan.Commands[current.VerificationResults.Count];
            var singlePlan = CodingPatchValidator.CreateVerificationPlan([command]);
            if (!singlePlan.IsSuccess) return Invalid("A durable verification command became invalid.");
            var execution = await verifier.VerifyAsync(
                current.Workspace, current.Authority, singlePlan.Value, cancellationToken);
            if (!execution.IsSuccess) return DomainResult.Fail<CodingSessionSnapshot>(execution.Failure!);
            var result = execution.Value.Results.Single();
            var recorded = CodingSessionStateMachine.RecordVerificationResult(current, result, clock.UtcNow);
            if (!recorded.IsSuccess) return recorded;
            var committed = await AppendAndCommitAsync(
                recorded.Value, "coding.verification-command-recorded", cancellationToken);
            if (!committed.IsSuccess) return committed;
            current = committed.Value;
        }

        return DomainResult.Success(current);
    }

    public async Task<DomainResult<CodingSessionSnapshot>> ReviewAsync(
        CodingSessionId sessionId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(sessionId, expectedVersion, CodingSessionState.Verified, cancellationToken);
        if (!loaded.IsSuccess) return loaded;
        var current = loaded.Value;
        var report = await reviewer.ReviewAsync(
            current.Workspace, current.PatchReceipt!, current.VerificationReceipt!, cancellationToken);
        if (!report.IsSuccess) return DomainResult.Fail<CodingSessionSnapshot>(report.Failure!);
        var reviewed = CodingSessionStateMachine.RecordReview(current, report.Value, clock.UtcNow);
        return reviewed.IsSuccess
            ? await AppendAndCommitAsync(reviewed.Value, "coding.review-recorded", cancellationToken)
            : reviewed;
    }

    public async Task<DomainResult<CodingSessionSnapshot>> CompleteAsync(
        CodingSessionId sessionId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(sessionId, expectedVersion, CodingSessionState.Reviewed, cancellationToken);
        if (!loaded.IsSuccess) return loaded;
        var completed = CodingSessionStateMachine.Complete(loaded.Value, clock.UtcNow);
        return completed.IsSuccess
            ? await AppendAndCommitAsync(completed.Value, "coding.session-completed", cancellationToken)
            : completed;
    }

    public async Task<DomainResult<CodingSessionSnapshot>> ResumeAsync(
        CodingSessionId sessionId,
        CancellationToken cancellationToken)
    {
        var current = await repository.FindLatestAsync(sessionId, cancellationToken);
        if (current is null || !CodingSessionStateMachine.IsConsistent(current))
        {
            return Invalid("The durable coding session does not exist or failed integrity validation.");
        }

        for (var transition = 0; transition < 8; transition++)
        {
            DomainResult<CodingSessionSnapshot> next;
            switch (current.State)
            {
                case CodingSessionState.PatchProposed:
                    next = await ApplyAsync(current.Id, current.Version, cancellationToken);
                    break;
                case CodingSessionState.Patched:
                case CodingSessionState.Verifying:
                    next = await VerifyAsync(current.Id, current.Version, cancellationToken);
                    break;
                case CodingSessionState.Verified:
                    next = await ReviewAsync(current.Id, current.Version, cancellationToken);
                    break;
                case CodingSessionState.Reviewed:
                    next = await CompleteAsync(current.Id, current.Version, cancellationToken);
                    break;
                case CodingSessionState.Completed:
                case CodingSessionState.Failed:
                case CodingSessionState.Cancelled:
                    return DomainResult.Success(current);
                default:
                    return Invalid("The prepared session requires its exact objective before backend proposal.");
            }

            if (!next.IsSuccess) return next;
            current = next.Value;
        }

        return DomainResult.Fail<CodingSessionSnapshot>(new DomainFailure(
            FailureCode.NoProgress, "Coding resume exceeded its bounded transition count."));
    }

    private async Task<DomainResult<CodingPatchSet>> OpenPatchAsync(
        CodingSessionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.PatchArtifact is not { } artifact || artifact.Length is <= 0 or > 4_194_304)
        {
            return DomainResult.Fail<CodingPatchSet>(new DomainFailure(
                FailureCode.ValidationFailure, "The durable patch artifact is missing or oversized."));
        }

        try
        {
            await using var input = await artifacts.OpenReadAsync(artifact, cancellationToken);
            using var output = new MemoryStream((int)artifact.Length);
            await input.CopyToAsync(output, cancellationToken);
            var bytes = output.ToArray();
            var hash = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
            var patch = JsonSerializer.Deserialize<CodingPatchSet>(bytes, SerializerOptions);
            return bytes.Length == artifact.Length && hash == artifact.ContentHash &&
                CodingPatchValidator.IsValid(patch) && patch!.PatchHash == snapshot.PatchHash
                ? DomainResult.Success(patch)
                : DomainResult.Fail<CodingPatchSet>(new DomainFailure(
                    FailureCode.ValidationFailure, "The durable patch artifact failed integrity validation."));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return DomainResult.Fail<CodingPatchSet>(new DomainFailure(
                FailureCode.RecoverableExternalFailure, "The durable patch artifact could not be opened safely."));
        }
    }

    private async Task<DomainResult<CodingSessionSnapshot>> LoadAsync(
        CodingSessionId id,
        long expectedVersion,
        CodingSessionState? requiredState,
        CancellationToken cancellationToken)
    {
        var current = await repository.FindLatestAsync(id, cancellationToken);
        return current is null
            ? Invalid("The coding session does not exist.")
            : !CodingSessionStateMachine.IsConsistent(current) || current.Version != expectedVersion ||
              requiredState is not null && current.State != requiredState
                ? Conflict("The coding session version or state is stale.")
                : DomainResult.Success(current);
    }

    private async Task<DomainResult<CodingSessionSnapshot>> AppendAndCommitAsync(
        CodingSessionSnapshot snapshot,
        string operation,
        CancellationToken cancellationToken)
    {
        await repository.AppendAsync(snapshot, cancellationToken);
        await audit.RecordAsync(new AuditRecordRequest(
            snapshot.InstallationId, snapshot.ActorId, snapshot.CorrelationId, snapshot.CausationId,
            operation, snapshot.State is CodingSessionState.Failed ? AuditOutcome.Failed : AuditOutcome.Succeeded,
            new { SessionId = snapshot.Id.ToString(), snapshot.Version, State = snapshot.State.ToString() },
            new
            {
                snapshot.SnapshotHash,
                snapshot.PatchHash,
                VerificationCount = snapshot.VerificationResults.Count,
                ReviewHash = snapshot.ReviewReport?.ReportHash,
            },
            snapshot.Failure?.Code.ToString()), cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded ? DomainResult.Success(snapshot) : DomainResult.Fail<CodingSessionSnapshot>(commit.Failure!);
    }

    private static DomainResult<CodingSessionSnapshot> Invalid(string message) =>
        DomainResult.Fail<CodingSessionSnapshot>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<CodingSessionSnapshot> Conflict(string message) =>
        DomainResult.Fail<CodingSessionSnapshot>(new DomainFailure(FailureCode.ConcurrencyConflict, message));
}
