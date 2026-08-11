using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Orchestration;

public readonly record struct OrchestrationTaskId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public readonly record struct TaskNodeId(string Value)
{
    public override string ToString() => Value;
}

public enum OrchestrationPattern
{
    Sequential,
    Concurrent,
    Handoff,
    ManagerWorker,
    Reviewer,
}

public enum OrchestrationTaskState
{
    Planned,
    Running,
    Waiting,
    Completed,
    Failed,
    Canceled,
    DeadLettered,
}

public enum TaskNodeState
{
    Pending,
    Ready,
    Leased,
    Completed,
    Failed,
    Compensated,
    DeadLettered,
}

public sealed record TaskExecutionBudget(
    int MaximumToolCalls,
    long MaximumInputTokens,
    long MaximumOutputTokens,
    int MaximumWallClockSeconds);

public sealed record TaskRetryPolicy(int MaximumAttempts, int DelaySeconds);

public sealed record TaskNodeDefinition(
    TaskNodeId Id,
    string Name,
    IReadOnlyList<TaskNodeId> Dependencies,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> ContextEvidenceHashes,
    TaskExecutionBudget Budget,
    TaskRetryPolicy Retry,
    TaskNodeId? CompensationNodeId = null);

public sealed record OrchestrationTaskDefinition(
    OrchestrationTaskId Id,
    InstallationId InstallationId,
    AgentIdentityId AgentId,
    long AgentVersion,
    OrchestrationPattern Pattern,
    IReadOnlyList<TaskNodeDefinition> Nodes,
    int MaximumConcurrency,
    int MaximumDelegationDepth,
    int MaximumChildren,
    string PolicySnapshotHash,
    string BudgetSnapshotHash,
    string SkillSnapshotHash);

public sealed record TaskNodeLease(
    string Owner,
    string TokenHash,
    DateTimeOffset AcquiredAt,
    DateTimeOffset HeartbeatAt,
    DateTimeOffset ExpiresAt);

public sealed record TaskNodeSnapshot(
    TaskNodeDefinition Definition,
    TaskNodeState State,
    int Attempt,
    DateTimeOffset? RetryNotBefore,
    TaskNodeLease? Lease,
    string EvidenceHash,
    FailureCode? FailureCode);

public sealed record OrchestrationTaskSnapshot(
    OrchestrationTaskDefinition Definition,
    long Version,
    OrchestrationTaskState State,
    IReadOnlyList<TaskNodeSnapshot> Nodes,
    string PreviousSnapshotHash,
    string SnapshotHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ActorId ActorId,
    string IdempotencyKey,
    CorrelationId CorrelationId,
    CorrelationId? CausationId);

public static class OrchestrationTaskStateMachine
{
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    public const string EmptyHash =
        "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public static DomainResult<OrchestrationTaskSnapshot> Create(
        OrchestrationTaskDefinition definition,
        ActorId actorId,
        string idempotencyKey,
        CorrelationId correlationId,
        CorrelationId? causationId,
        DateTimeOffset createdAt)
    {
        var validation = ValidateDefinition(definition);
        if (!validation.IsSuccess || !IsBounded(actorId.Value, 256) ||
            !IsBounded(idempotencyKey, 256) || !IsBounded(correlationId.Value, 128) ||
            causationId is { } causation && !IsBounded(causation.Value, 128))
        {
            return Invalid(validation.Failure?.Message ?? "Task authority and idempotency must be bounded.");
        }

        var snapshotDefinition = SnapshotDefinition(definition);
        var compensationIds = snapshotDefinition.Nodes
            .Where(node => node.CompensationNodeId is not null)
            .Select(node => node.CompensationNodeId!.Value)
            .ToHashSet();
        var nodes = snapshotDefinition.Nodes.Select(node => new TaskNodeSnapshot(
            node,
            node.Dependencies.Count == 0 && !compensationIds.Contains(node.Id)
                ? TaskNodeState.Ready
                : TaskNodeState.Pending,
            0,
            null,
            null,
            EmptyHash,
            null)).ToArray();
        var snapshot = new OrchestrationTaskSnapshot(
            snapshotDefinition,
            0,
            OrchestrationTaskState.Planned,
            nodes,
            EmptyHash,
            EmptyHash,
            createdAt,
            createdAt,
            actorId,
            idempotencyKey,
            correlationId,
            causationId);
        return DomainResult.Success(snapshot with { SnapshotHash = ComputeHash(snapshot) });
    }

    public static DomainResult<OrchestrationTaskSnapshot> Claim(
        OrchestrationTaskSnapshot current,
        TaskNodeId nodeId,
        string owner,
        string tokenHash,
        DateTimeOffset expiresAt,
        DateTimeOffset occurredAt)
    {
        if (!CanMutate(current, occurredAt) || !IsNodeId(nodeId) || !IsBounded(owner, 256) ||
            !IsHash(tokenHash) || expiresAt <= occurredAt || expiresAt > occurredAt.AddMinutes(5))
        {
            return Invalid("Claim requires a current task, ready node, bounded owner, hash-only token, and short lease.");
        }

        var index = FindNode(current, nodeId);
        var leasedCount = current.Nodes.Count(node => node.State is TaskNodeState.Leased);
        if (index < 0 || current.Nodes[index].State is not TaskNodeState.Ready ||
            current.Nodes[index].RetryNotBefore > occurredAt ||
            leasedCount >= current.Definition.MaximumConcurrency)
        {
            return Conflict("The node is not currently claimable within task concurrency.");
        }

        var node = current.Nodes[index];
        return Next(current, Replace(current.Nodes, index, node with
        {
            State = TaskNodeState.Leased,
            Attempt = node.Attempt + 1,
            RetryNotBefore = null,
            Lease = new TaskNodeLease(owner, tokenHash, occurredAt, occurredAt, expiresAt),
            FailureCode = null,
        }), occurredAt);
    }

    public static DomainResult<OrchestrationTaskSnapshot> Heartbeat(
        OrchestrationTaskSnapshot current,
        TaskNodeId nodeId,
        string owner,
        string tokenHash,
        DateTimeOffset newExpiry,
        DateTimeOffset occurredAt)
    {
        if (!CanMutate(current, occurredAt) || !IsBounded(owner, 256) || !IsHash(tokenHash))
        {
            return Invalid("Heartbeat requires current hash-bound lease authority.");
        }

        var index = FindNode(current, nodeId);
        if (index < 0 || current.Nodes[index] is not { State: TaskNodeState.Leased, Lease: { } lease } ||
            !string.Equals(lease.Owner, owner, StringComparison.Ordinal) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(lease.TokenHash),
                Encoding.ASCII.GetBytes(tokenHash)) ||
            occurredAt < lease.HeartbeatAt || occurredAt >= lease.ExpiresAt ||
            newExpiry <= lease.ExpiresAt || newExpiry > occurredAt.AddMinutes(5))
        {
            return Conflict("The task lease is stale, expired, or owned by another worker.");
        }

        var node = current.Nodes[index];
        return Next(current, Replace(current.Nodes, index, node with
        {
            Lease = lease with { HeartbeatAt = occurredAt, ExpiresAt = newExpiry },
        }), occurredAt);
    }

    public static DomainResult<OrchestrationTaskSnapshot> Complete(
        OrchestrationTaskSnapshot current,
        TaskNodeId nodeId,
        string owner,
        string tokenHash,
        string evidenceHash,
        DateTimeOffset occurredAt)
    {
        var lease = ValidateLease(current, nodeId, owner, tokenHash, evidenceHash, occurredAt);
        if (!lease.IsSuccess)
        {
            return DomainResult.Fail<OrchestrationTaskSnapshot>(lease.Failure!);
        }

        var index = lease.Value;
        var node = current.Nodes[index];
        var compensationIds = CompensationIds(current.Definition);
        var nextState = compensationIds.Contains(node.Definition.Id)
            ? TaskNodeState.Compensated
            : TaskNodeState.Completed;
        return Next(current, Replace(current.Nodes, index, node with
        {
            State = nextState,
            Lease = null,
            EvidenceHash = evidenceHash,
            FailureCode = null,
        }), occurredAt);
    }

    public static DomainResult<OrchestrationTaskSnapshot> Fail(
        OrchestrationTaskSnapshot current,
        TaskNodeId nodeId,
        string owner,
        string tokenHash,
        string evidenceHash,
        FailureCode failureCode,
        bool retryable,
        DateTimeOffset occurredAt)
    {
        var lease = ValidateLease(current, nodeId, owner, tokenHash, evidenceHash, occurredAt);
        if (!lease.IsSuccess || !Enum.IsDefined(failureCode))
        {
            return lease.IsSuccess
                ? Invalid("Node failure requires a typed failure code.")
                : DomainResult.Fail<OrchestrationTaskSnapshot>(lease.Failure!);
        }

        var index = lease.Value;
        return ApplyFailure(current, index, evidenceHash, failureCode, retryable, occurredAt);
    }

    public static DomainResult<OrchestrationTaskSnapshot> RecoverExpired(
        OrchestrationTaskSnapshot current,
        DateTimeOffset occurredAt)
    {
        if (!CanMutate(current, occurredAt))
        {
            return Invalid("Only a current nonterminal task can recover expired leases.");
        }

        var expired = current.Nodes
            .Select((node, index) => (node, index))
            .Where(item => item.node is { State: TaskNodeState.Leased, Lease: { } lease } &&
                lease.ExpiresAt <= occurredAt)
            .OrderBy(item => item.node.Definition.Id.Value, StringComparer.Ordinal)
            .ToArray();
        if (expired.Length == 0)
        {
            return Conflict("No expired task lease was available for recovery.");
        }

        var nodes = current.Nodes.ToArray();
        foreach (var (node, index) in expired)
        {
            nodes[index] = FailedNode(
                node,
                EmptyHash,
                FailureCode.RecoverableExternalFailure,
                retryable: true,
                occurredAt);
        }

        ActivateCompensations(current.Definition, nodes);
        return Next(current, nodes, occurredAt);
    }

    public static DomainResult<OrchestrationTaskSnapshot> Cancel(
        OrchestrationTaskSnapshot current,
        DateTimeOffset occurredAt)
    {
        if (!CanMutate(current, occurredAt))
        {
            return Invalid("Only a current nonterminal task can be canceled.");
        }

        return Next(current, current.Nodes, occurredAt, OrchestrationTaskState.Canceled);
    }

    public static bool IsTerminal(OrchestrationTaskState state) =>
        state is OrchestrationTaskState.Completed or OrchestrationTaskState.Failed or
            OrchestrationTaskState.Canceled or OrchestrationTaskState.DeadLettered;

    public static bool IsConsistent(OrchestrationTaskSnapshot? snapshot) =>
        snapshot is not null && snapshot.Version >= 0 && snapshot.UpdatedAt >= snapshot.CreatedAt &&
        ValidateDefinition(snapshot.Definition).IsSuccess && snapshot.Nodes.Count == snapshot.Definition.Nodes.Count &&
        snapshot.Nodes.Select(node => node.Definition.Id).SequenceEqual(
            snapshot.Definition.Nodes.Select(node => node.Id)) &&
        snapshot.Nodes.All(IsValidNodeSnapshot) && IsHash(snapshot.PreviousSnapshotHash) &&
        IsHash(snapshot.SnapshotHash) && IsBounded(snapshot.ActorId.Value, 256) &&
        IsBounded(snapshot.IdempotencyKey, 256) && IsBounded(snapshot.CorrelationId.Value, 128) &&
        (snapshot.CausationId is null || IsBounded(snapshot.CausationId.Value.Value, 128)) &&
        string.Equals(snapshot.SnapshotHash, ComputeHash(snapshot), StringComparison.Ordinal);

    public static DomainResult<bool> ValidateDefinition(OrchestrationTaskDefinition? definition)
    {
        if (definition is null || definition.Id.Value == Guid.Empty || definition.InstallationId.Value == Guid.Empty ||
            definition.AgentId.Value == Guid.Empty || definition.AgentVersion < 0 || !Enum.IsDefined(definition.Pattern) ||
            definition.Nodes is null || definition.Nodes.Count is < 1 or > 1_024 ||
            definition.MaximumConcurrency is < 1 or > 128 ||
            definition.MaximumConcurrency > definition.Nodes.Count ||
            definition.MaximumDelegationDepth is < 0 or > 16 || definition.MaximumChildren is < 0 or > 256 ||
            !IsHash(definition.PolicySnapshotHash) || !IsHash(definition.BudgetSnapshotHash) ||
            !IsHash(definition.SkillSnapshotHash))
        {
            return ValidationFailure("Task definition identity, authority, bounds, and snapshots are invalid.");
        }

        var ids = new HashSet<TaskNodeId>();
        foreach (var node in definition.Nodes)
        {
            if (!IsNodeId(node.Id) || !ids.Add(node.Id) || !IsBounded(node.Name, 256) ||
                node.Dependencies is null || node.Dependencies.Count > 1_024 ||
                node.Dependencies.Distinct().Count() != node.Dependencies.Count ||
                node.RequiredCapabilities is null || node.RequiredCapabilities.Count > 256 ||
                node.RequiredCapabilities.Any(capability => !IsBounded(capability, 256)) ||
                node.RequiredCapabilities.Distinct(StringComparer.Ordinal).Count() != node.RequiredCapabilities.Count ||
                node.ContextEvidenceHashes is null || node.ContextEvidenceHashes.Count > 256 ||
                node.ContextEvidenceHashes.Any(hash => !IsHash(hash)) ||
                !IsValid(node.Budget) || !IsValid(node.Retry))
            {
                return ValidationFailure("A task node has invalid identity, dependencies, capabilities, evidence, or budget.");
            }
        }

        if (definition.Nodes.Any(node => node.Dependencies.Any(dependency => !ids.Contains(dependency)) ||
            node.Dependencies.Contains(node.Id) ||
            node.CompensationNodeId is { } compensation &&
                (!ids.Contains(compensation) || compensation == node.Id)) || HasCycle(definition.Nodes))
        {
            return ValidationFailure("Task dependencies and compensation targets must exist and form an acyclic graph.");
        }

        var compensationTargets = definition.Nodes
            .Where(node => node.CompensationNodeId is not null)
            .Select(node => node.CompensationNodeId!.Value)
            .ToArray();
        if (compensationTargets.Distinct().Count() != compensationTargets.Length ||
            definition.Nodes.Where(node => compensationTargets.Contains(node.Id))
                .Any(node => node.CompensationNodeId is not null))
        {
            return ValidationFailure("Compensation nodes must be unique leaves of compensation authority.");
        }

        return DomainResult.Success(true);
    }

    private static DomainResult<OrchestrationTaskSnapshot> ApplyFailure(
        OrchestrationTaskSnapshot current,
        int index,
        string evidenceHash,
        FailureCode failureCode,
        bool retryable,
        DateTimeOffset occurredAt)
    {
        var nodes = current.Nodes.ToArray();
        nodes[index] = FailedNode(nodes[index], evidenceHash, failureCode, retryable, occurredAt);
        ActivateCompensations(current.Definition, nodes);
        return Next(current, nodes, occurredAt);
    }

    private static TaskNodeSnapshot FailedNode(
        TaskNodeSnapshot node,
        string evidenceHash,
        FailureCode failureCode,
        bool retryable,
        DateTimeOffset occurredAt)
    {
        var retry = retryable && node.Attempt < node.Definition.Retry.MaximumAttempts;
        return node with
        {
            State = retry ? TaskNodeState.Pending : TaskNodeState.Failed,
            RetryNotBefore = retry ? occurredAt.AddSeconds(node.Definition.Retry.DelaySeconds) : null,
            Lease = null,
            EvidenceHash = evidenceHash,
            FailureCode = failureCode,
        };
    }

    private static void ActivateCompensations(
        OrchestrationTaskDefinition definition,
        TaskNodeSnapshot[] nodes)
    {
        foreach (var failed in nodes.Where(node => node.State is TaskNodeState.Failed &&
            node.Definition.CompensationNodeId is not null))
        {
            var compensationIndex = Array.FindIndex(
                nodes,
                node => node.Definition.Id == failed.Definition.CompensationNodeId!.Value);
            if (compensationIndex >= 0 && nodes[compensationIndex].State is TaskNodeState.Pending)
            {
                nodes[compensationIndex] = nodes[compensationIndex] with { State = TaskNodeState.Ready };
            }
        }
    }

    private static DomainResult<int> ValidateLease(
        OrchestrationTaskSnapshot current,
        TaskNodeId nodeId,
        string owner,
        string tokenHash,
        string evidenceHash,
        DateTimeOffset occurredAt)
    {
        if (!CanMutate(current, occurredAt) || !IsBounded(owner, 256) ||
            !IsHash(tokenHash) || !IsHash(evidenceHash))
        {
            return DomainResult.Fail<int>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Node completion requires current lease authority and bounded evidence."));
        }

        var index = FindNode(current, nodeId);
        if (index < 0 || current.Nodes[index] is not { State: TaskNodeState.Leased, Lease: { } lease } ||
            lease.ExpiresAt < occurredAt || !string.Equals(lease.Owner, owner, StringComparison.Ordinal) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(lease.TokenHash),
                Encoding.ASCII.GetBytes(tokenHash)))
        {
            return DomainResult.Fail<int>(new DomainFailure(
                FailureCode.ConcurrencyConflict,
                "The task-node lease is missing, expired, stale, or owned by another worker."));
        }

        return DomainResult.Success(index);
    }

    private static DomainResult<OrchestrationTaskSnapshot> Next(
        OrchestrationTaskSnapshot current,
        IReadOnlyList<TaskNodeSnapshot> rawNodes,
        DateTimeOffset occurredAt,
        OrchestrationTaskState? forcedState = null)
    {
        var nodes = rawNodes.ToArray();
        RefreshReady(current.Definition, nodes, occurredAt);
        var state = forcedState ?? CalculateState(current.Definition, nodes);
        var next = current with
        {
            Version = current.Version + 1,
            State = state,
            Nodes = nodes,
            PreviousSnapshotHash = current.SnapshotHash,
            SnapshotHash = EmptyHash,
            UpdatedAt = occurredAt,
        };
        return DomainResult.Success(next with { SnapshotHash = ComputeHash(next) });
    }

    private static void RefreshReady(
        OrchestrationTaskDefinition definition,
        TaskNodeSnapshot[] nodes,
        DateTimeOffset occurredAt)
    {
        var compensationIds = CompensationIds(definition);
        var states = nodes.ToDictionary(node => node.Definition.Id, node => node.State);
        for (var index = 0; index < nodes.Length; index++)
        {
            var node = nodes[index];
            if (node.State is TaskNodeState.Pending && !compensationIds.Contains(node.Definition.Id) &&
                (node.RetryNotBefore is null || node.RetryNotBefore <= occurredAt) &&
                node.Definition.Dependencies.All(dependency => states[dependency] is TaskNodeState.Completed))
            {
                nodes[index] = node with { State = TaskNodeState.Ready, RetryNotBefore = null };
            }
        }
    }

    private static OrchestrationTaskState CalculateState(
        OrchestrationTaskDefinition definition,
        IReadOnlyList<TaskNodeSnapshot> nodes)
    {
        var compensationIds = CompensationIds(definition);
        var primary = nodes.Where(node => !compensationIds.Contains(node.Definition.Id)).ToArray();
        if (primary.All(node => node.State is TaskNodeState.Completed))
        {
            return OrchestrationTaskState.Completed;
        }

        var failed = primary.Where(node => node.State is TaskNodeState.Failed).ToArray();
        if (failed.Length > 0)
        {
            var compensationsComplete = failed.All(node => node.Definition.CompensationNodeId is null ||
                nodes.Single(candidate => candidate.Definition.Id == node.Definition.CompensationNodeId.Value)
                    .State is TaskNodeState.Compensated);
            return compensationsComplete ? OrchestrationTaskState.Failed : OrchestrationTaskState.Waiting;
        }

        if (nodes.Any(node => node.State is TaskNodeState.Leased))
        {
            return OrchestrationTaskState.Running;
        }

        return nodes.Any(node => node.State is TaskNodeState.Ready)
            ? OrchestrationTaskState.Planned
            : OrchestrationTaskState.Waiting;
    }

    private static bool HasCycle(IReadOnlyList<TaskNodeDefinition> nodes)
    {
        var dependencies = nodes.ToDictionary(node => node.Id, node => node.Dependencies);
        var visiting = new HashSet<TaskNodeId>();
        var visited = new HashSet<TaskNodeId>();
        bool Visit(TaskNodeId id)
        {
            if (visiting.Contains(id))
            {
                return true;
            }

            if (!visited.Add(id))
            {
                return false;
            }

            visiting.Add(id);
            var cycle = dependencies[id].Any(Visit);
            visiting.Remove(id);
            return cycle;
        }

        return nodes.Any(node => Visit(node.Id));
    }

    private static OrchestrationTaskDefinition SnapshotDefinition(OrchestrationTaskDefinition definition) =>
        definition with
        {
            Nodes = definition.Nodes.Select(node => node with
            {
                Dependencies = node.Dependencies.ToArray(),
                RequiredCapabilities = node.RequiredCapabilities.ToArray(),
                ContextEvidenceHashes = node.ContextEvidenceHashes.ToArray(),
            }).ToArray(),
        };

    private static HashSet<TaskNodeId> CompensationIds(OrchestrationTaskDefinition definition) =>
        definition.Nodes.Where(node => node.CompensationNodeId is not null)
            .Select(node => node.CompensationNodeId!.Value)
            .ToHashSet();

    private static int FindNode(OrchestrationTaskSnapshot snapshot, TaskNodeId id) =>
        snapshot.Nodes.ToList().FindIndex(node => node.Definition.Id == id);

    private static TaskNodeSnapshot[] Replace(
        IReadOnlyList<TaskNodeSnapshot> nodes,
        int index,
        TaskNodeSnapshot replacement)
    {
        var copy = nodes.ToArray();
        copy[index] = replacement;
        return copy;
    }

    private static bool CanMutate(OrchestrationTaskSnapshot? snapshot, DateTimeOffset occurredAt) =>
        IsConsistent(snapshot) && !IsTerminal(snapshot!.State) && occurredAt >= snapshot.UpdatedAt;

    private static bool IsValidNodeSnapshot(TaskNodeSnapshot node) =>
        Enum.IsDefined(node.State) && node.Attempt >= 0 && node.Attempt <= node.Definition.Retry.MaximumAttempts &&
        IsHash(node.EvidenceHash) && (node.Lease is null ||
            IsBounded(node.Lease.Owner, 256) && IsHash(node.Lease.TokenHash) &&
            node.Lease.HeartbeatAt >= node.Lease.AcquiredAt && node.Lease.ExpiresAt > node.Lease.HeartbeatAt);

    private static bool IsValid(TaskExecutionBudget? budget) => budget is not null &&
        budget.MaximumToolCalls is >= 0 and <= 1_024 &&
        budget.MaximumInputTokens is >= 0 and <= 10_000_000 &&
        budget.MaximumOutputTokens is >= 1 and <= 1_000_000 &&
        budget.MaximumWallClockSeconds is >= 1 and <= 86_400;

    private static bool IsValid(TaskRetryPolicy? retry) => retry is not null &&
        retry.MaximumAttempts is >= 1 and <= 32 && retry.DelaySeconds is >= 0 and <= 86_400;

    private static bool IsNodeId(TaskNodeId id) => IsBounded(id.Value, 128) &&
        char.IsAsciiLetterOrDigit(id.Value[0]) &&
        id.Value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool IsHash(string? value) => value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept(LowerHex) < 0;

    private static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => !char.IsControl(character));

    private static string ComputeHash(OrchestrationTaskSnapshot snapshot)
    {
        var builder = new StringBuilder(4096);
        Append(builder, snapshot.Definition.Id.ToString());
        Append(builder, snapshot.Definition.InstallationId.ToString());
        Append(builder, snapshot.Definition.AgentId.ToString());
        Append(builder, snapshot.Definition.AgentVersion);
        Append(builder, snapshot.Definition.Pattern);
        Append(builder, snapshot.Definition.MaximumConcurrency);
        Append(builder, snapshot.Definition.MaximumDelegationDepth);
        Append(builder, snapshot.Definition.MaximumChildren);
        Append(builder, snapshot.Definition.PolicySnapshotHash);
        Append(builder, snapshot.Definition.BudgetSnapshotHash);
        Append(builder, snapshot.Definition.SkillSnapshotHash);
        foreach (var node in snapshot.Nodes)
        {
            Append(builder, node.Definition.Id.Value);
            Append(builder, node.Definition.Name);
            foreach (var dependency in node.Definition.Dependencies)
            {
                Append(builder, dependency.Value);
            }

            foreach (var capability in node.Definition.RequiredCapabilities)
            {
                Append(builder, capability);
            }

            foreach (var contextHash in node.Definition.ContextEvidenceHashes)
            {
                Append(builder, contextHash);
            }

            Append(builder, node.Definition.Budget.MaximumToolCalls);
            Append(builder, node.Definition.Budget.MaximumInputTokens);
            Append(builder, node.Definition.Budget.MaximumOutputTokens);
            Append(builder, node.Definition.Budget.MaximumWallClockSeconds);
            Append(builder, node.Definition.Retry.MaximumAttempts);
            Append(builder, node.Definition.Retry.DelaySeconds);
            Append(builder, node.Definition.CompensationNodeId?.Value ?? string.Empty);
            Append(builder, node.State);
            Append(builder, node.Attempt);
            Append(builder, node.RetryNotBefore?.UtcTicks ?? 0);
            Append(builder, node.Lease?.Owner ?? string.Empty);
            Append(builder, node.Lease?.TokenHash ?? string.Empty);
            Append(builder, node.Lease?.AcquiredAt.UtcTicks ?? 0);
            Append(builder, node.Lease?.HeartbeatAt.UtcTicks ?? 0);
            Append(builder, node.Lease?.ExpiresAt.UtcTicks ?? 0);
            Append(builder, node.EvidenceHash);
            Append(builder, node.FailureCode?.ToString() ?? string.Empty);
        }

        Append(builder, snapshot.Version);
        Append(builder, snapshot.State);
        Append(builder, snapshot.PreviousSnapshotHash);
        Append(builder, snapshot.CreatedAt.UtcTicks);
        Append(builder, snapshot.UpdatedAt.UtcTicks);
        Append(builder, snapshot.ActorId.Value);
        Append(builder, snapshot.IdempotencyKey);
        Append(builder, snapshot.CorrelationId.Value);
        Append(builder, snapshot.CausationId?.Value ?? string.Empty);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))}";
    }

    private static void Append(StringBuilder builder, object value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        builder.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append(';');
    }

    private static DomainResult<OrchestrationTaskSnapshot> Invalid(string message) =>
        DomainResult.Fail<OrchestrationTaskSnapshot>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<OrchestrationTaskSnapshot> Conflict(string message) =>
        DomainResult.Fail<OrchestrationTaskSnapshot>(new DomainFailure(FailureCode.ConcurrencyConflict, message));

    private static DomainResult<bool> ValidationFailure(string message) =>
        DomainResult.Fail<bool>(new DomainFailure(FailureCode.ValidationFailure, message));
}
