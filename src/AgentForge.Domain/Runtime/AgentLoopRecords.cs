using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Runtime;

public readonly record struct AgentLoopId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public enum AgentLoopPhase
{
    Observe,
    Plan,
    Act,
    Verify,
    Reflect,
    Persist,
}

public enum AgentLoopState
{
    Running,
    Completed,
    Failed,
    Canceled,
    BudgetExceeded,
    NoProgress,
}

public sealed record AgentLoopBudget(
    int MaximumTurns,
    int MaximumToolCalls,
    long MaximumInputTokens,
    long MaximumOutputTokens,
    int MaximumWallClockSeconds,
    int MaximumStructuredRepairs,
    int MaximumConsecutiveNoProgress);

public sealed record AgentLoopConsumption(
    long InputTokens,
    long OutputTokens,
    int ToolCalls,
    int WallClockSeconds);

public sealed record AgentLoopSnapshot(
    AgentLoopId LoopId,
    InstallationId InstallationId,
    AgentIdentityId AgentId,
    long AgentVersion,
    long Sequence,
    int Turn,
    AgentLoopPhase Phase,
    AgentLoopState State,
    AgentLoopBudget Budget,
    AgentLoopConsumption Consumption,
    int StructuredRepairCount,
    int ConsecutiveNoProgress,
    bool CompletionPending,
    string InitialStateHash,
    string? LastProgressEvidenceHash,
    string StepEvidenceHash,
    string PreviousSnapshotHash,
    string SnapshotHash,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    ActorId ActorId,
    string IdempotencyKey,
    CorrelationId CorrelationId,
    CorrelationId? CausationId,
    FailureCode? FailureCode);

public sealed record AgentLoopStepResult(
    string StepEvidenceHash,
    string? ProgressEvidenceHash,
    long InputTokens,
    long OutputTokens,
    int ToolCalls,
    bool StructuredOutputValid,
    bool RequestsCompletion);

public static class AgentLoopStateMachine
{
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    public static DomainResult<AgentLoopSnapshot> Create(
        AgentLoopId loopId,
        InstallationId installationId,
        AgentIdentityId agentId,
        long agentVersion,
        AgentLoopBudget budget,
        string initialStateHash,
        ActorId actorId,
        string idempotencyKey,
        CorrelationId correlationId,
        CorrelationId? causationId,
        DateTimeOffset startedAt)
    {
        if (loopId.Value == Guid.Empty || installationId.Value == Guid.Empty || agentId.Value == Guid.Empty ||
            agentVersion < 0 || !IsValid(budget) || !IsHash(initialStateHash) ||
            !IsBounded(actorId.Value, 256) || !IsBounded(idempotencyKey, 256) ||
            !IsBounded(correlationId.Value, 128) ||
            causationId is { } causation && !IsBounded(causation.Value, 128))
        {
            return Invalid("An agent loop requires bounded identity, authority, budget, and initial-state evidence.");
        }

        var snapshot = new AgentLoopSnapshot(
            loopId,
            installationId,
            agentId,
            agentVersion,
            0,
            1,
            AgentLoopPhase.Observe,
            AgentLoopState.Running,
            budget,
            new AgentLoopConsumption(0, 0, 0, 0),
            0,
            0,
            false,
            initialStateHash,
            null,
            EmptyHash,
            EmptyHash,
            EmptyHash,
            startedAt,
            startedAt,
            actorId,
            idempotencyKey,
            correlationId,
            causationId,
            null);
        return DomainResult.Success(snapshot with { SnapshotHash = ComputeHash(snapshot) });
    }

    public static DomainResult<AgentLoopSnapshot> Advance(
        AgentLoopSnapshot current,
        AgentLoopStepResult step,
        DateTimeOffset occurredAt)
    {
        if (!IsConsistent(current) || current.State is not AgentLoopState.Running || step is null ||
            !IsHash(step.StepEvidenceHash) ||
            step.ProgressEvidenceHash is { } progress && !IsHash(progress) ||
            step.InputTokens is < 0 or > 10_000_000 || step.OutputTokens is < 0 or > 1_000_000 ||
            step.ToolCalls is < 0 or > 1_024 || occurredAt < current.UpdatedAt ||
            step.RequestsCompletion && current.Phase is not AgentLoopPhase.Verify)
        {
            return Invalid("An agent loop step requires the current snapshot and bounded evidence.");
        }

        if (!TryAdd(current.Consumption.InputTokens, step.InputTokens, out var inputTokens) ||
            !TryAdd(current.Consumption.OutputTokens, step.OutputTokens, out var outputTokens) ||
            !TryAdd(current.Consumption.ToolCalls, step.ToolCalls, out var toolCalls))
        {
            return BudgetExceeded(current, step.StepEvidenceHash, occurredAt);
        }

        var elapsedSeconds = Math.Ceiling((occurredAt - current.StartedAt).TotalSeconds);
        var elapsedWallClock = elapsedSeconds >= int.MaxValue ? int.MaxValue : (int)elapsedSeconds;
        var wallClockSeconds = Math.Max(current.Consumption.WallClockSeconds, elapsedWallClock);
        var consumption = new AgentLoopConsumption(inputTokens, outputTokens, toolCalls, wallClockSeconds);
        if (inputTokens > current.Budget.MaximumInputTokens ||
            outputTokens > current.Budget.MaximumOutputTokens ||
            toolCalls > current.Budget.MaximumToolCalls ||
            wallClockSeconds > current.Budget.MaximumWallClockSeconds)
        {
            return BudgetExceeded(current, step.StepEvidenceHash, occurredAt, consumption);
        }

        if (!step.StructuredOutputValid)
        {
            if (current.StructuredRepairCount >= current.Budget.MaximumStructuredRepairs)
            {
                return Terminal(
                    current,
                    AgentLoopState.Failed,
                    FailureCode.ValidationFailure,
                    step.StepEvidenceHash,
                    occurredAt,
                    consumption);
            }

            return Next(
                current,
                current.Phase,
                current.Turn,
                step.StepEvidenceHash,
                occurredAt,
                consumption,
                current.StructuredRepairCount + 1,
                current.ConsecutiveNoProgress,
                current.CompletionPending,
                current.LastProgressEvidenceHash);
        }

        var completionPending = current.CompletionPending || step.RequestsCompletion;
        if (current.Phase is not AgentLoopPhase.Persist)
        {
            return Next(
                current,
                (AgentLoopPhase)((int)current.Phase + 1),
                current.Turn,
                step.StepEvidenceHash,
                occurredAt,
                consumption,
                current.StructuredRepairCount,
                current.ConsecutiveNoProgress,
                completionPending,
                current.LastProgressEvidenceHash);
        }

        if (step.ProgressEvidenceHash is null)
        {
            return Invalid("Persist steps require normalized progress evidence.");
        }

        var noProgress = string.Equals(
            current.LastProgressEvidenceHash,
            step.ProgressEvidenceHash,
            StringComparison.Ordinal) ? current.ConsecutiveNoProgress + 1 : 0;
        if (completionPending)
        {
            return Terminal(
                current,
                AgentLoopState.Completed,
                null,
                step.StepEvidenceHash,
                occurredAt,
                consumption,
                step.ProgressEvidenceHash,
                noProgress,
                completionPending);
        }

        if (noProgress >= current.Budget.MaximumConsecutiveNoProgress)
        {
            return Terminal(
                current,
                AgentLoopState.NoProgress,
                FailureCode.NoProgress,
                step.StepEvidenceHash,
                occurredAt,
                consumption,
                step.ProgressEvidenceHash,
                noProgress,
                false);
        }

        if (current.Turn >= current.Budget.MaximumTurns)
        {
            return BudgetExceeded(
                current,
                step.StepEvidenceHash,
                occurredAt,
                consumption,
                step.ProgressEvidenceHash,
                noProgress);
        }

        return Next(
            current,
            AgentLoopPhase.Observe,
            current.Turn + 1,
            step.StepEvidenceHash,
            occurredAt,
            consumption,
            current.StructuredRepairCount,
            noProgress,
            false,
            step.ProgressEvidenceHash);
    }

    public static DomainResult<AgentLoopSnapshot> Fail(
        AgentLoopSnapshot current,
        DomainFailure failure,
        string stepEvidenceHash,
        DateTimeOffset occurredAt)
    {
        if (failure is null || !Enum.IsDefined(failure.Code) || !IsHash(stepEvidenceHash))
        {
            return Invalid("An agent loop failure requires typed, hash-bound evidence.");
        }

        return Terminal(current, AgentLoopState.Failed, failure.Code, stepEvidenceHash, occurredAt);
    }

    public static DomainResult<AgentLoopSnapshot> Cancel(
        AgentLoopSnapshot current,
        string stepEvidenceHash,
        DateTimeOffset occurredAt) =>
        !IsHash(stepEvidenceHash)
            ? Invalid("Agent loop cancellation requires hash-bound evidence.")
            : Terminal(current, AgentLoopState.Canceled, null, stepEvidenceHash, occurredAt);

    public static bool IsTerminal(AgentLoopState state) => state is not AgentLoopState.Running;

    public static bool IsConsistent(AgentLoopSnapshot? snapshot) =>
        snapshot is not null && snapshot.LoopId.Value != Guid.Empty &&
        snapshot.InstallationId.Value != Guid.Empty && snapshot.AgentId.Value != Guid.Empty &&
        snapshot.AgentVersion >= 0 && snapshot.Sequence >= 0 && snapshot.Turn >= 1 &&
        Enum.IsDefined(snapshot.Phase) && Enum.IsDefined(snapshot.State) && IsValid(snapshot.Budget) &&
        snapshot.Consumption.InputTokens >= 0 && snapshot.Consumption.OutputTokens >= 0 &&
        snapshot.Consumption.ToolCalls >= 0 && snapshot.Consumption.WallClockSeconds >= 0 &&
        snapshot.StructuredRepairCount is >= 0 and <= 32 && snapshot.ConsecutiveNoProgress >= 0 &&
        IsHash(snapshot.InitialStateHash) &&
        (snapshot.LastProgressEvidenceHash is null || IsHash(snapshot.LastProgressEvidenceHash)) &&
        IsHash(snapshot.StepEvidenceHash) && IsHash(snapshot.PreviousSnapshotHash) &&
        IsHash(snapshot.SnapshotHash) && snapshot.UpdatedAt >= snapshot.StartedAt &&
        IsBounded(snapshot.ActorId.Value, 256) && IsBounded(snapshot.IdempotencyKey, 256) &&
        IsBounded(snapshot.CorrelationId.Value, 128) &&
        (snapshot.CausationId is null || IsBounded(snapshot.CausationId.Value.Value, 128)) &&
        string.Equals(snapshot.SnapshotHash, ComputeHash(snapshot), StringComparison.Ordinal);

    public const string EmptyHash =
        "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static DomainResult<AgentLoopSnapshot> Next(
        AgentLoopSnapshot current,
        AgentLoopPhase phase,
        int turn,
        string stepEvidenceHash,
        DateTimeOffset occurredAt,
        AgentLoopConsumption consumption,
        int repairs,
        int noProgress,
        bool completionPending,
        string? lastProgress)
    {
        var next = current with
        {
            Sequence = current.Sequence + 1,
            Turn = turn,
            Phase = phase,
            Consumption = consumption,
            StructuredRepairCount = repairs,
            ConsecutiveNoProgress = noProgress,
            CompletionPending = completionPending,
            LastProgressEvidenceHash = lastProgress,
            StepEvidenceHash = stepEvidenceHash,
            PreviousSnapshotHash = current.SnapshotHash,
            SnapshotHash = EmptyHash,
            UpdatedAt = occurredAt,
        };
        return DomainResult.Success(next with { SnapshotHash = ComputeHash(next) });
    }

    private static DomainResult<AgentLoopSnapshot> Terminal(
        AgentLoopSnapshot current,
        AgentLoopState state,
        FailureCode? failureCode,
        string stepEvidenceHash,
        DateTimeOffset occurredAt,
        AgentLoopConsumption? consumption = null,
        string? lastProgress = null,
        int? noProgress = null,
        bool? completionPending = null)
    {
        if (!IsConsistent(current) || current.State is not AgentLoopState.Running || occurredAt < current.UpdatedAt)
        {
            return Invalid("Only the current running agent-loop snapshot can become terminal.");
        }

        var terminal = current with
        {
            Sequence = current.Sequence + 1,
            State = state,
            Consumption = consumption ?? current.Consumption,
            LastProgressEvidenceHash = lastProgress ?? current.LastProgressEvidenceHash,
            ConsecutiveNoProgress = noProgress ?? current.ConsecutiveNoProgress,
            CompletionPending = completionPending ?? current.CompletionPending,
            StepEvidenceHash = stepEvidenceHash,
            PreviousSnapshotHash = current.SnapshotHash,
            SnapshotHash = EmptyHash,
            UpdatedAt = occurredAt,
            FailureCode = failureCode,
        };
        return DomainResult.Success(terminal with { SnapshotHash = ComputeHash(terminal) });
    }

    private static DomainResult<AgentLoopSnapshot> BudgetExceeded(
        AgentLoopSnapshot current,
        string stepEvidenceHash,
        DateTimeOffset occurredAt,
        AgentLoopConsumption? consumption = null,
        string? lastProgress = null,
        int? noProgress = null) =>
        Terminal(
            current,
            AgentLoopState.BudgetExceeded,
            FailureCode.BudgetExceeded,
            stepEvidenceHash,
            occurredAt,
            consumption,
            lastProgress,
            noProgress);

    private static string ComputeHash(AgentLoopSnapshot snapshot)
    {
        var builder = new StringBuilder(1024);
        Append(builder, snapshot.LoopId.ToString());
        Append(builder, snapshot.InstallationId.ToString());
        Append(builder, snapshot.AgentId.ToString());
        Append(builder, snapshot.AgentVersion);
        Append(builder, snapshot.Sequence);
        Append(builder, snapshot.Turn);
        Append(builder, snapshot.Phase.ToString());
        Append(builder, snapshot.State.ToString());
        Append(builder, snapshot.Budget.MaximumTurns);
        Append(builder, snapshot.Budget.MaximumToolCalls);
        Append(builder, snapshot.Budget.MaximumInputTokens);
        Append(builder, snapshot.Budget.MaximumOutputTokens);
        Append(builder, snapshot.Budget.MaximumWallClockSeconds);
        Append(builder, snapshot.Budget.MaximumStructuredRepairs);
        Append(builder, snapshot.Budget.MaximumConsecutiveNoProgress);
        Append(builder, snapshot.Consumption.InputTokens);
        Append(builder, snapshot.Consumption.OutputTokens);
        Append(builder, snapshot.Consumption.ToolCalls);
        Append(builder, snapshot.Consumption.WallClockSeconds);
        Append(builder, snapshot.StructuredRepairCount);
        Append(builder, snapshot.ConsecutiveNoProgress);
        Append(builder, snapshot.CompletionPending);
        Append(builder, snapshot.InitialStateHash);
        Append(builder, snapshot.LastProgressEvidenceHash ?? string.Empty);
        Append(builder, snapshot.StepEvidenceHash);
        Append(builder, snapshot.PreviousSnapshotHash);
        Append(builder, snapshot.StartedAt.UtcTicks);
        Append(builder, snapshot.UpdatedAt.UtcTicks);
        Append(builder, snapshot.ActorId.Value);
        Append(builder, snapshot.IdempotencyKey);
        Append(builder, snapshot.CorrelationId.Value);
        Append(builder, snapshot.CausationId?.Value ?? string.Empty);
        Append(builder, snapshot.FailureCode?.ToString() ?? string.Empty);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))}";
    }

    private static void Append(StringBuilder builder, object value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        builder.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append(';');
    }

    private static bool IsValid(AgentLoopBudget? budget) => budget is not null &&
        budget.MaximumTurns is >= 1 and <= 128 && budget.MaximumToolCalls is >= 0 and <= 1_024 &&
        budget.MaximumInputTokens is >= 0 and <= 10_000_000 &&
        budget.MaximumOutputTokens is >= 1 and <= 1_000_000 &&
        budget.MaximumWallClockSeconds is >= 1 and <= 86_400 &&
        budget.MaximumStructuredRepairs is >= 0 and <= 32 &&
        budget.MaximumConsecutiveNoProgress is >= 1 and <= 32;

    private static bool IsHash(string? value) => value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept(LowerHex) < 0;

    private static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => !char.IsControl(character));

    private static bool TryAdd(long left, long right, out long sum)
    {
        try
        {
            sum = checked(left + right);
            return true;
        }
        catch (OverflowException)
        {
            sum = 0;
            return false;
        }
    }

    private static bool TryAdd(int left, int right, out int sum)
    {
        try
        {
            sum = checked(left + right);
            return true;
        }
        catch (OverflowException)
        {
            sum = 0;
            return false;
        }
    }

    private static DomainResult<AgentLoopSnapshot> Invalid(string message) =>
        DomainResult.Fail<AgentLoopSnapshot>(new DomainFailure(FailureCode.ValidationFailure, message));
}
