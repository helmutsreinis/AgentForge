using System.Security.Cryptography;
using System.Text;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Orchestration;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;

namespace AgentForge.Orchestration;

internal sealed class TaskOrchestrator(
    ITaskSnapshotStore snapshots,
    IAuditRecorder audit,
    IUnitOfWork unitOfWork,
    IClock clock) : ITaskOrchestrator
{
    public async Task<DomainResult<TaskTransitionResult>> CreateAsync(
        OrchestrationTaskDefinition definition,
        ActorId actorId,
        string idempotencyKey,
        CorrelationId correlationId,
        CorrelationId? causationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var existing = await snapshots.FindByIdempotencyKeyAsync(
            definition.InstallationId,
            idempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            return Matches(definition, actorId, correlationId, causationId, existing)
                ? DomainResult.Success(new TaskTransitionResult(existing, true))
                : Conflict<TaskTransitionResult>("Task idempotency is already bound to different authority or definition.");
        }

        var created = OrchestrationTaskStateMachine.Create(
            definition,
            actorId,
            idempotencyKey,
            correlationId,
            causationId,
            clock.UtcNow);
        return created.IsSuccess
            ? await PersistAsync(created.Value, "orchestration.task-created", cancellationToken)
            : DomainResult.Fail<TaskTransitionResult>(created.Failure!);
    }

    public async Task<DomainResult<TaskLeaseGrant>> ClaimAsync(
        OrchestrationTaskId taskId,
        long expectedVersion,
        TaskNodeId nodeId,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromMinutes(5))
        {
            return Invalid<TaskLeaseGrant>("Task leases must be positive and no longer than five minutes.");
        }

        var current = await ReadCurrentAsync(taskId, expectedVersion, cancellationToken);
        if (!current.IsSuccess)
        {
            return DomainResult.Fail<TaskLeaseGrant>(current.Failure!);
        }

        Span<byte> random = stackalloc byte[32];
        RandomNumberGenerator.Fill(random);
        var token = Convert.ToHexStringLower(random);
        CryptographicOperations.ZeroMemory(random);
        var transition = OrchestrationTaskStateMachine.Claim(
            current.Value,
            nodeId,
            owner,
            HashToken(token),
            clock.UtcNow.Add(leaseDuration),
            clock.UtcNow);
        if (!transition.IsSuccess)
        {
            return DomainResult.Fail<TaskLeaseGrant>(transition.Failure!);
        }

        var stored = await PersistAsync(transition.Value, "orchestration.node-claimed", cancellationToken);
        return stored.IsSuccess
            ? DomainResult.Success(new TaskLeaseGrant(stored.Value.Snapshot, nodeId, token,
                stored.Value.Snapshot.Nodes.Single(node => node.Definition.Id == nodeId).Lease!.ExpiresAt))
            : DomainResult.Fail<TaskLeaseGrant>(stored.Failure!);
    }

    public Task<DomainResult<TaskTransitionResult>> HeartbeatAsync(
        OrchestrationTaskId taskId,
        long expectedVersion,
        TaskNodeId nodeId,
        string owner,
        string leaseToken,
        TimeSpan extension,
        CancellationToken cancellationToken) => MutateAsync(
            taskId,
            expectedVersion,
            current => extension <= TimeSpan.Zero || extension > TimeSpan.FromMinutes(5)
                ? InvalidSnapshot("Heartbeat extension must be positive and no longer than five minutes.")
                : OrchestrationTaskStateMachine.Heartbeat(
                    current,
                    nodeId,
                    owner,
                    HashToken(leaseToken),
                    clock.UtcNow.Add(extension),
                    clock.UtcNow),
            "orchestration.node-heartbeat",
            cancellationToken);

    public Task<DomainResult<TaskTransitionResult>> CompleteAsync(
        OrchestrationTaskId taskId,
        long expectedVersion,
        TaskNodeId nodeId,
        string owner,
        string leaseToken,
        string evidenceHash,
        CancellationToken cancellationToken) => MutateAsync(
            taskId,
            expectedVersion,
            current => OrchestrationTaskStateMachine.Complete(
                current,
                nodeId,
                owner,
                HashToken(leaseToken),
                evidenceHash,
                clock.UtcNow),
            "orchestration.node-completed",
            cancellationToken);

    public Task<DomainResult<TaskTransitionResult>> FailAsync(
        OrchestrationTaskId taskId,
        long expectedVersion,
        TaskNodeId nodeId,
        string owner,
        string leaseToken,
        string evidenceHash,
        FailureCode failureCode,
        bool retryable,
        CancellationToken cancellationToken) => MutateAsync(
            taskId,
            expectedVersion,
            current => OrchestrationTaskStateMachine.Fail(
                current,
                nodeId,
                owner,
                HashToken(leaseToken),
                evidenceHash,
                failureCode,
                retryable,
                clock.UtcNow),
            "orchestration.node-failed",
            cancellationToken);

    public Task<DomainResult<TaskTransitionResult>> RecoverExpiredAsync(
        OrchestrationTaskId taskId,
        long expectedVersion,
        CancellationToken cancellationToken) => MutateAsync(
            taskId,
            expectedVersion,
            current => OrchestrationTaskStateMachine.RecoverExpired(current, clock.UtcNow),
            "orchestration.lease-recovered",
            cancellationToken);

    public Task<DomainResult<TaskTransitionResult>> CancelAsync(
        OrchestrationTaskId taskId,
        long expectedVersion,
        CancellationToken cancellationToken) => MutateAsync(
            taskId,
            expectedVersion,
            current => OrchestrationTaskStateMachine.Cancel(current, clock.UtcNow),
            "orchestration.task-canceled",
            cancellationToken);

    private async Task<DomainResult<TaskTransitionResult>> MutateAsync(
        OrchestrationTaskId taskId,
        long expectedVersion,
        Func<OrchestrationTaskSnapshot, DomainResult<OrchestrationTaskSnapshot>> transition,
        string operation,
        CancellationToken cancellationToken)
    {
        var current = await ReadCurrentAsync(taskId, expectedVersion, cancellationToken);
        if (!current.IsSuccess)
        {
            return DomainResult.Fail<TaskTransitionResult>(current.Failure!);
        }

        DomainResult<OrchestrationTaskSnapshot> next;
        try
        {
            next = transition(current.Value);
        }
        catch (InvalidOperationException)
        {
            return Invalid<TaskTransitionResult>("The requested task node or lease does not exist.");
        }

        return next.IsSuccess
            ? await PersistAsync(next.Value, operation, cancellationToken)
            : DomainResult.Fail<TaskTransitionResult>(next.Failure!);
    }

    private async Task<DomainResult<OrchestrationTaskSnapshot>> ReadCurrentAsync(
        OrchestrationTaskId taskId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var current = await snapshots.FindLatestAsync(taskId, cancellationToken);
        return current is null
            ? DomainResult.Fail<OrchestrationTaskSnapshot>(new DomainFailure(
                FailureCode.ValidationFailure,
                "The orchestration task does not exist."))
            : current.Version != expectedVersion
                ? DomainResult.Fail<OrchestrationTaskSnapshot>(new DomainFailure(
                    FailureCode.ConcurrencyConflict,
                    "The orchestration task version is stale."))
                : DomainResult.Success(current);
    }

    private async Task<DomainResult<TaskTransitionResult>> PersistAsync(
        OrchestrationTaskSnapshot snapshot,
        string operation,
        CancellationToken cancellationToken)
    {
        await snapshots.AppendAsync(snapshot, cancellationToken);
        await audit.RecordAsync(new AuditRecordRequest(
            snapshot.Definition.InstallationId,
            snapshot.ActorId,
            snapshot.CorrelationId,
            snapshot.CausationId,
            operation,
            ToOutcome(snapshot.State),
            new
            {
                TaskId = snapshot.Definition.Id.ToString(),
                snapshot.Version,
                snapshot.PreviousSnapshotHash,
            },
            new
            {
                State = snapshot.State.ToString(),
                snapshot.SnapshotHash,
                Nodes = snapshot.Nodes.Select(node => new
                {
                    Id = node.Definition.Id.Value,
                    State = node.State.ToString(),
                    node.Attempt,
                    node.EvidenceHash,
                    FailureCode = node.FailureCode?.ToString(),
                }),
            },
            null), cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded
            ? DomainResult.Success(new TaskTransitionResult(snapshot))
            : DomainResult.Fail<TaskTransitionResult>(commit.Failure!);
    }

    private static string HashToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256 || token.Any(char.IsControl))
        {
            return OrchestrationTaskStateMachine.EmptyHash;
        }

        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)))}";
    }

    private static bool Matches(
        OrchestrationTaskDefinition definition,
        ActorId actorId,
        CorrelationId correlationId,
        CorrelationId? causationId,
        OrchestrationTaskSnapshot existing) =>
        DefinitionMatches(definition, existing.Definition) && actorId == existing.ActorId &&
        correlationId == existing.CorrelationId && causationId == existing.CausationId;

    private static bool DefinitionMatches(
        OrchestrationTaskDefinition left,
        OrchestrationTaskDefinition right) =>
        left.Id == right.Id && left.InstallationId == right.InstallationId && left.AgentId == right.AgentId &&
        left.AgentVersion == right.AgentVersion && left.Pattern == right.Pattern &&
        left.MaximumConcurrency == right.MaximumConcurrency &&
        left.MaximumDelegationDepth == right.MaximumDelegationDepth &&
        left.MaximumChildren == right.MaximumChildren &&
        string.Equals(left.PolicySnapshotHash, right.PolicySnapshotHash, StringComparison.Ordinal) &&
        string.Equals(left.BudgetSnapshotHash, right.BudgetSnapshotHash, StringComparison.Ordinal) &&
        string.Equals(left.SkillSnapshotHash, right.SkillSnapshotHash, StringComparison.Ordinal) &&
        left.Nodes.Count == right.Nodes.Count && left.Nodes.Zip(right.Nodes).All(pair =>
            pair.First.Id == pair.Second.Id &&
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal) &&
            pair.First.Dependencies.SequenceEqual(pair.Second.Dependencies) &&
            pair.First.RequiredCapabilities.SequenceEqual(pair.Second.RequiredCapabilities, StringComparer.Ordinal) &&
            pair.First.ContextEvidenceHashes.SequenceEqual(pair.Second.ContextEvidenceHashes, StringComparer.Ordinal) &&
            pair.First.Budget == pair.Second.Budget && pair.First.Retry == pair.Second.Retry &&
            pair.First.CompensationNodeId == pair.Second.CompensationNodeId);

    private static AuditOutcome ToOutcome(OrchestrationTaskState state) => state switch
    {
        OrchestrationTaskState.Canceled => AuditOutcome.Canceled,
        OrchestrationTaskState.Failed or OrchestrationTaskState.DeadLettered => AuditOutcome.Failed,
        _ => AuditOutcome.Succeeded,
    };

    private static DomainResult<OrchestrationTaskSnapshot> InvalidSnapshot(string message) =>
        DomainResult.Fail<OrchestrationTaskSnapshot>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<T> Invalid<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<T> Conflict<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ConcurrencyConflict, message));
}
