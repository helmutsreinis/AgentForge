using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Scheduling;

public readonly record struct ScheduleId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public enum ScheduleTriggerKind
{
    OneShot,
    Interval,
    Cron,
    Calendar,
}

public enum ScheduleState
{
    Active,
    Paused,
    Expired,
    DeadLettered,
}

public enum ScheduleMisfirePolicy
{
    Skip,
    FireOnce,
    CatchUp,
}

public enum ScheduleOverlapPolicy
{
    Skip,
    Queue,
    Parallel,
}

public enum ScheduleOccurrenceState
{
    Queued,
    Running,
}

public sealed record CalendarScheduleRule(
    int Hour,
    int Minute,
    IReadOnlyList<DayOfWeek> DaysOfWeek,
    int? DayOfMonth);

public sealed record ScheduleTrigger(
    ScheduleTriggerKind Kind,
    DateTimeOffset? OneShotAt,
    DateTimeOffset? IntervalAnchor,
    int? IntervalSeconds,
    string? CronExpression,
    CalendarScheduleRule? Calendar);

public sealed record ScheduleDefinition(
    ScheduleId Id,
    InstallationId InstallationId,
    AgentIdentityId AgentId,
    long AgentVersion,
    ScheduleTrigger Trigger,
    string TimeZoneId,
    ScheduleMisfirePolicy MisfirePolicy,
    ScheduleOverlapPolicy OverlapPolicy,
    int MisfireGraceSeconds,
    int MaximumCatchUp,
    int MaximumParallelRuns,
    int MaximumJitterSeconds,
    int MaximumAttempts,
    int RetryDelaySeconds,
    int MaximumConsecutiveFailures,
    DateTimeOffset? ExpiresAt,
    string PolicySnapshotHash,
    string CapabilitySnapshotHash,
    string BudgetSnapshotHash,
    string SkillSnapshotHash);

public sealed record ScheduleRunLease(
    string Owner,
    string TokenHash,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt);

public sealed record ScheduleOccurrence(
    string IdempotencyKeyHash,
    DateTimeOffset ScheduledFor,
    DateTimeOffset DueAt,
    ScheduleOccurrenceState State,
    int Attempt,
    DateTimeOffset? RetryNotBefore,
    ScheduleRunLease? Lease,
    string EvidenceHash,
    bool IsRunNow);

public sealed record ScheduleSnapshot(
    ScheduleDefinition Definition,
    long Version,
    ScheduleState State,
    DateTimeOffset? NextScheduledFor,
    DateTimeOffset? NextDueAt,
    IReadOnlyList<ScheduleOccurrence> Occurrences,
    long CompletedCount,
    long FailedCount,
    long SkippedCount,
    int ConsecutiveFailures,
    string? LastOccurrenceIdHash,
    string LastEvidenceHash,
    string PreviousSnapshotHash,
    string SnapshotHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ActorId ActorId,
    string IdempotencyKey,
    CorrelationId CorrelationId,
    CorrelationId? CausationId);

public static class ScheduleStateMachine
{
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    public const string EmptyHash =
        "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public static DomainResult<ScheduleSnapshot> Create(
        ScheduleDefinition definition,
        TimeZoneInfo timeZone,
        ActorId actorId,
        string idempotencyKey,
        CorrelationId correlationId,
        CorrelationId? causationId,
        DateTimeOffset createdAt)
    {
        if (!IsValid(definition, timeZone) || !IsBounded(actorId.Value, 256) ||
            !IsBounded(idempotencyKey, 256) || !IsBounded(correlationId.Value, 128) ||
            causationId is { } causation && !IsBounded(causation.Value, 128))
        {
            return Invalid("Schedule identity, recurrence, authority, and bounds are invalid.");
        }

        var next = ScheduleCalculator.Next(definition, null, createdAt.AddTicks(-1), timeZone);
        if (!next.IsSuccess)
        {
            return DomainResult.Fail<ScheduleSnapshot>(next.Failure!);
        }

        var state = next.Value is null || definition.ExpiresAt is { } expires && next.Value > expires
            ? ScheduleState.Expired
            : ScheduleState.Active;
        var snapshot = new ScheduleSnapshot(
            Snapshot(definition),
            0,
            state,
            state is ScheduleState.Active ? next.Value : null,
            state is ScheduleState.Active ? ApplyJitter(definition, next.Value!.Value) : null,
            [],
            0,
            0,
            0,
            0,
            null,
            EmptyHash,
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

    public static DomainResult<ScheduleSnapshot> EvaluateDue(
        ScheduleSnapshot current,
        TimeZoneInfo timeZone,
        DateTimeOffset occurredAt)
    {
        if (!CanMutate(current, occurredAt) || current.State is not ScheduleState.Active ||
            !string.Equals(current.Definition.TimeZoneId, timeZone.Id, StringComparison.Ordinal))
        {
            return Invalid("Only a current active schedule can evaluate due work in its pinned timezone.");
        }

        if (current.NextDueAt is null || current.NextScheduledFor is null || current.NextDueAt > occurredAt)
        {
            return Conflict("The schedule has no due occurrence.");
        }

        var due = new List<(DateTimeOffset Scheduled, DateTimeOffset Due)>();
        var scheduled = current.NextScheduledFor.Value;
        var dueAt = current.NextDueAt.Value;
        DateTimeOffset? nextScheduled = scheduled;
        DateTimeOffset? nextDue = dueAt;
        for (var index = 0; index < 512 && nextDue <= occurredAt; index++)
        {
            due.Add((nextScheduled!.Value, nextDue.Value));
            var calculated = ScheduleCalculator.Next(current.Definition, nextScheduled, occurredAt, timeZone);
            if (!calculated.IsSuccess)
            {
                return DomainResult.Fail<ScheduleSnapshot>(calculated.Failure!);
            }

            nextScheduled = calculated.Value;
            nextDue = nextScheduled is null ? null : ApplyJitter(current.Definition, nextScheduled.Value);
        }

        if (nextDue <= occurredAt)
        {
            return DomainResult.Fail<ScheduleSnapshot>(new DomainFailure(
                FailureCode.BudgetExceeded,
                "Schedule catch-up calculation exceeded its bounded scan."));
        }

        var running = current.Occurrences.Count(item => item.State is ScheduleOccurrenceState.Running);
        var selected = SelectDue(current.Definition, due, occurredAt);
        long skipped = due.Count - selected.Count;
        if (running > 0 && current.Definition.OverlapPolicy is ScheduleOverlapPolicy.Skip)
        {
            skipped += selected.Count;
            selected = [];
        }

        if (current.Occurrences.Count + selected.Count > 128)
        {
            return DomainResult.Fail<ScheduleSnapshot>(new DomainFailure(
                FailureCode.BudgetExceeded,
                "The bounded schedule queue is full."));
        }

        var occurrences = current.Occurrences.Concat(selected.Select(item => new ScheduleOccurrence(
            OccurrenceHash(current.Definition.Id, item.Scheduled, "scheduled"),
            item.Scheduled,
            item.Due,
            ScheduleOccurrenceState.Queued,
            0,
            null,
            null,
            EmptyHash,
            false))).ToArray();
        var state = nextScheduled is null || current.Definition.ExpiresAt is { } expires && nextScheduled > expires
            ? ScheduleState.Expired
            : ScheduleState.Active;
        return Next(
            current,
            occurredAt,
            state,
            state is ScheduleState.Active ? nextScheduled : null,
            state is ScheduleState.Active ? nextDue : null,
            occurrences,
            skippedCount: checked(current.SkippedCount + skipped));
    }

    public static DomainResult<ScheduleSnapshot> RunNow(
        ScheduleSnapshot current,
        string idempotencyKeyHash,
        DateTimeOffset occurredAt)
    {
        if (!CanMutate(current, occurredAt) || current.State is ScheduleState.DeadLettered ||
            !IsHash(idempotencyKeyHash) || current.Occurrences.Count >= 128)
        {
            return Invalid("Run-now requires current non-dead-lettered authority and bounded idempotency.");
        }

        if (current.Occurrences.Any(item =>
                string.Equals(item.IdempotencyKeyHash, idempotencyKeyHash, StringComparison.Ordinal)))
        {
            return Conflict("The run-now idempotency key already exists in the active queue.");
        }

        var occurrence = new ScheduleOccurrence(
            idempotencyKeyHash,
            occurredAt,
            occurredAt,
            ScheduleOccurrenceState.Queued,
            0,
            null,
            null,
            EmptyHash,
            true);
        return Next(current, occurredAt, current.State, current.NextScheduledFor, current.NextDueAt,
            [.. current.Occurrences, occurrence]);
    }

    public static DomainResult<ScheduleSnapshot> Claim(
        ScheduleSnapshot current,
        string occurrenceIdHash,
        string owner,
        string tokenHash,
        DateTimeOffset expiresAt,
        DateTimeOffset occurredAt)
    {
        if (!CanMutate(current, occurredAt) || !IsHash(occurrenceIdHash) || !IsBounded(owner, 256) ||
            !IsHash(tokenHash) || expiresAt <= occurredAt || expiresAt > occurredAt.AddMinutes(5))
        {
            return Invalid("Schedule claim requires exact queued identity and a short hash-bound lease.");
        }

        var index = Find(current, occurrenceIdHash);
        var running = current.Occurrences.Count(item => item.State is ScheduleOccurrenceState.Running);
        var parallelLimit = current.Definition.OverlapPolicy is ScheduleOverlapPolicy.Parallel
            ? current.Definition.MaximumParallelRuns
            : 1;
        if (index < 0 || current.State is ScheduleState.DeadLettered ||
            current.Occurrences[index] is not { State: ScheduleOccurrenceState.Queued } queued ||
            current.State is ScheduleState.Paused && !queued.IsRunNow ||
            (queued.RetryNotBefore ?? queued.DueAt) > occurredAt || running >= parallelLimit)
        {
            return Conflict("The occurrence is not claimable under its due and overlap policy.");
        }

        var copy = current.Occurrences.ToArray();
        copy[index] = queued with
        {
            State = ScheduleOccurrenceState.Running,
            Attempt = queued.Attempt + 1,
            RetryNotBefore = null,
            Lease = new ScheduleRunLease(owner, tokenHash, occurredAt, expiresAt),
        };
        return Next(current, occurredAt, current.State, current.NextScheduledFor, current.NextDueAt, copy);
    }

    public static DomainResult<ScheduleSnapshot> Complete(
        ScheduleSnapshot current,
        string occurrenceIdHash,
        string owner,
        string tokenHash,
        string evidenceHash,
        DateTimeOffset occurredAt)
    {
        var validated = ValidateLease(current, occurrenceIdHash, owner, tokenHash, evidenceHash, occurredAt);
        if (!validated.IsSuccess)
        {
            return DomainResult.Fail<ScheduleSnapshot>(validated.Failure!);
        }

        var occurrences = current.Occurrences.Where((_, index) => index != validated.Value).ToArray();
        return Next(
            current,
            occurredAt,
            current.State,
            current.NextScheduledFor,
            current.NextDueAt,
            occurrences,
            completedCount: checked(current.CompletedCount + 1),
            consecutiveFailures: 0,
            lastOccurrenceIdHash: occurrenceIdHash,
            lastEvidenceHash: evidenceHash);
    }

    public static DomainResult<ScheduleSnapshot> Fail(
        ScheduleSnapshot current,
        string occurrenceIdHash,
        string owner,
        string tokenHash,
        string evidenceHash,
        DateTimeOffset occurredAt)
    {
        var validated = ValidateLease(current, occurrenceIdHash, owner, tokenHash, evidenceHash, occurredAt);
        if (!validated.IsSuccess)
        {
            return DomainResult.Fail<ScheduleSnapshot>(validated.Failure!);
        }

        var copy = current.Occurrences.ToArray();
        var occurrence = copy[validated.Value];
        if (occurrence.Attempt < current.Definition.MaximumAttempts)
        {
            copy[validated.Value] = occurrence with
            {
                State = ScheduleOccurrenceState.Queued,
                RetryNotBefore = occurredAt.AddSeconds(current.Definition.RetryDelaySeconds),
                Lease = null,
                EvidenceHash = evidenceHash,
            };
            return Next(current, occurredAt, current.State, current.NextScheduledFor, current.NextDueAt, copy);
        }

        var occurrences = copy.Where((_, index) => index != validated.Value).ToArray();
        var failures = current.ConsecutiveFailures + 1;
        var state = failures >= current.Definition.MaximumConsecutiveFailures
            ? ScheduleState.DeadLettered
            : current.State;
        return Next(
            current,
            occurredAt,
            state,
            current.NextScheduledFor,
            current.NextDueAt,
            occurrences,
            failedCount: checked(current.FailedCount + 1),
            consecutiveFailures: failures,
            lastOccurrenceIdHash: occurrenceIdHash,
            lastEvidenceHash: evidenceHash);
    }

    public static DomainResult<ScheduleSnapshot> RecoverExpired(
        ScheduleSnapshot current,
        DateTimeOffset occurredAt)
    {
        if (!CanMutate(current, occurredAt))
        {
            return Invalid("Only a current schedule can recover expired run leases.");
        }

        var copy = current.Occurrences.ToList();
        var recovered = 0;
        var failed = 0;
        for (var index = copy.Count - 1; index >= 0; index--)
        {
            if (copy[index] is { State: ScheduleOccurrenceState.Running, Lease: { } lease } &&
                lease.ExpiresAt <= occurredAt)
            {
                var occurrence = copy[index];
                if (occurrence.Attempt >= current.Definition.MaximumAttempts)
                {
                    copy.RemoveAt(index);
                    failed++;
                    recovered++;
                    continue;
                }

                copy[index] = occurrence with
                {
                    State = ScheduleOccurrenceState.Queued,
                    RetryNotBefore = occurredAt.AddSeconds(current.Definition.RetryDelaySeconds),
                    Lease = null,
                    EvidenceHash = EmptyHash,
                };
                recovered++;
            }
        }

        if (recovered == 0)
        {
            return Conflict("No expired schedule run lease was available.");
        }

        var consecutiveFailures = checked(current.ConsecutiveFailures + failed);
        var state = failed > 0 && consecutiveFailures >= current.Definition.MaximumConsecutiveFailures
            ? ScheduleState.DeadLettered
            : current.State;
        return Next(
            current,
            occurredAt,
            state,
            current.NextScheduledFor,
            current.NextDueAt,
            copy,
            failedCount: checked(current.FailedCount + failed),
            consecutiveFailures: consecutiveFailures);
    }

    public static DomainResult<ScheduleSnapshot> Pause(
        ScheduleSnapshot current,
        DateTimeOffset occurredAt) =>
        !CanMutate(current, occurredAt) || current.State is not ScheduleState.Active
            ? Invalid("Only an active schedule can pause.")
            : Next(current, occurredAt, ScheduleState.Paused, current.NextScheduledFor, current.NextDueAt,
                current.Occurrences);

    public static DomainResult<ScheduleSnapshot> Resume(
        ScheduleSnapshot current,
        TimeZoneInfo timeZone,
        DateTimeOffset occurredAt)
    {
        if (!CanMutate(current, occurredAt) || current.State is not ScheduleState.Paused ||
            !string.Equals(current.Definition.TimeZoneId, timeZone.Id, StringComparison.Ordinal))
        {
            return Invalid("Only a paused schedule can resume in its pinned timezone.");
        }

        var state = current.Definition.ExpiresAt is { } expires && expires <= occurredAt
            ? ScheduleState.Expired
            : ScheduleState.Active;
        return Next(current, occurredAt, state, current.NextScheduledFor, current.NextDueAt,
            current.Occurrences);
    }

    public static bool IsConsistent(ScheduleSnapshot? snapshot) => snapshot is not null &&
        IsValid(snapshot.Definition, null) && snapshot.Version >= 0 && Enum.IsDefined(snapshot.State) &&
        snapshot.Occurrences.Count <= 128 && snapshot.Occurrences.All(IsValid) &&
        snapshot.CompletedCount >= 0 && snapshot.FailedCount >= 0 && snapshot.SkippedCount >= 0 &&
        snapshot.ConsecutiveFailures is >= 0 and <= 1_024 &&
        (snapshot.LastOccurrenceIdHash is null || IsHash(snapshot.LastOccurrenceIdHash)) &&
        IsHash(snapshot.LastEvidenceHash) && IsHash(snapshot.PreviousSnapshotHash) &&
        IsHash(snapshot.SnapshotHash) && snapshot.UpdatedAt >= snapshot.CreatedAt &&
        IsBounded(snapshot.ActorId.Value, 256) && IsBounded(snapshot.IdempotencyKey, 256) &&
        IsBounded(snapshot.CorrelationId.Value, 128) &&
        (snapshot.CausationId is null || IsBounded(snapshot.CausationId.Value.Value, 128)) &&
        string.Equals(snapshot.SnapshotHash, ComputeHash(snapshot), StringComparison.Ordinal);

    private static List<(DateTimeOffset Scheduled, DateTimeOffset Due)> SelectDue(
        ScheduleDefinition definition,
        List<(DateTimeOffset Scheduled, DateTimeOffset Due)> due,
        DateTimeOffset occurredAt)
    {
        var withinGrace = due.Where(item => occurredAt <= item.Due.AddSeconds(definition.MisfireGraceSeconds)).ToList();
        if (due.Count == 1 && withinGrace.Count == 1)
        {
            return withinGrace;
        }

        return definition.MisfirePolicy switch
        {
            ScheduleMisfirePolicy.Skip => withinGrace.TakeLast(1).ToList(),
            ScheduleMisfirePolicy.FireOnce => [due[^1]],
            ScheduleMisfirePolicy.CatchUp => due.Take(definition.MaximumCatchUp).ToList(),
            _ => [],
        };
    }

    private static DomainResult<int> ValidateLease(
        ScheduleSnapshot current,
        string occurrenceIdHash,
        string owner,
        string tokenHash,
        string evidenceHash,
        DateTimeOffset occurredAt)
    {
        if (!CanMutate(current, occurredAt) || !IsHash(occurrenceIdHash) || !IsBounded(owner, 256) ||
            !IsHash(tokenHash) || !IsHash(evidenceHash))
        {
            return DomainResult.Fail<int>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Schedule completion requires exact live lease authority and evidence."));
        }

        var index = Find(current, occurrenceIdHash);
        if (index < 0 || current.Occurrences[index] is not
            { State: ScheduleOccurrenceState.Running, Lease: { } lease } ||
            lease.ExpiresAt < occurredAt || !string.Equals(lease.Owner, owner, StringComparison.Ordinal) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(lease.TokenHash),
                Encoding.ASCII.GetBytes(tokenHash)))
        {
            return DomainResult.Fail<int>(new DomainFailure(
                FailureCode.ConcurrencyConflict,
                "The schedule run lease is stale, expired, or owned by another worker."));
        }

        return DomainResult.Success(index);
    }

    private static DomainResult<ScheduleSnapshot> Next(
        ScheduleSnapshot current,
        DateTimeOffset occurredAt,
        ScheduleState state,
        DateTimeOffset? nextScheduledFor,
        DateTimeOffset? nextDueAt,
        IReadOnlyList<ScheduleOccurrence> occurrences,
        long? completedCount = null,
        long? failedCount = null,
        long? skippedCount = null,
        int? consecutiveFailures = null,
        string? lastOccurrenceIdHash = null,
        string? lastEvidenceHash = null)
    {
        var next = current with
        {
            Version = current.Version + 1,
            State = state,
            NextScheduledFor = nextScheduledFor,
            NextDueAt = nextDueAt,
            Occurrences = occurrences.ToArray(),
            CompletedCount = completedCount ?? current.CompletedCount,
            FailedCount = failedCount ?? current.FailedCount,
            SkippedCount = skippedCount ?? current.SkippedCount,
            ConsecutiveFailures = consecutiveFailures ?? current.ConsecutiveFailures,
            LastOccurrenceIdHash = lastOccurrenceIdHash ?? current.LastOccurrenceIdHash,
            LastEvidenceHash = lastEvidenceHash ?? current.LastEvidenceHash,
            PreviousSnapshotHash = current.SnapshotHash,
            SnapshotHash = EmptyHash,
            UpdatedAt = occurredAt,
        };
        return DomainResult.Success(next with { SnapshotHash = ComputeHash(next) });
    }

    private static bool CanMutate(ScheduleSnapshot? current, DateTimeOffset occurredAt) =>
        IsConsistent(current) && occurredAt >= current!.UpdatedAt;

    private static int Find(ScheduleSnapshot snapshot, string occurrenceIdHash) =>
        snapshot.Occurrences.ToList().FindIndex(item =>
            string.Equals(item.IdempotencyKeyHash, occurrenceIdHash, StringComparison.Ordinal));

    private static bool IsValid(ScheduleOccurrence occurrence) =>
        IsHash(occurrence.IdempotencyKeyHash) && Enum.IsDefined(occurrence.State) &&
        occurrence.Attempt is >= 0 and <= 32 && IsHash(occurrence.EvidenceHash) &&
        (occurrence.Lease is null || IsBounded(occurrence.Lease.Owner, 256) &&
            IsHash(occurrence.Lease.TokenHash) && occurrence.Lease.ExpiresAt > occurrence.Lease.AcquiredAt);

    private static bool IsValid(ScheduleDefinition? definition, TimeZoneInfo? timeZone) =>
        definition is not null && definition.Id.Value != Guid.Empty &&
        definition.InstallationId.Value != Guid.Empty && definition.AgentId.Value != Guid.Empty &&
        definition.AgentVersion >= 0 && definition.Trigger is not null &&
        IsBounded(definition.TimeZoneId, 128) &&
        (timeZone is null || string.Equals(definition.TimeZoneId, timeZone.Id, StringComparison.Ordinal)) &&
        Enum.IsDefined(definition.MisfirePolicy) && Enum.IsDefined(definition.OverlapPolicy) &&
        definition.MisfireGraceSeconds is >= 0 and <= 86_400 && definition.MaximumCatchUp is >= 1 and <= 128 &&
        definition.MaximumParallelRuns is >= 1 and <= 32 && definition.MaximumJitterSeconds is >= 0 and <= 3_600 &&
        definition.MaximumAttempts is >= 1 and <= 32 && definition.RetryDelaySeconds is >= 0 and <= 86_400 &&
        definition.MaximumConsecutiveFailures is >= 1 and <= 128 &&
        IsHash(definition.PolicySnapshotHash) && IsHash(definition.CapabilitySnapshotHash) &&
        IsHash(definition.BudgetSnapshotHash) && IsHash(definition.SkillSnapshotHash) &&
        ScheduleCalculator.IsValid(definition.Trigger);

    private static ScheduleDefinition Snapshot(ScheduleDefinition definition) => definition with
    {
        Trigger = definition.Trigger with
        {
            Calendar = definition.Trigger.Calendar is null ? null : definition.Trigger.Calendar with
            {
                DaysOfWeek = definition.Trigger.Calendar.DaysOfWeek.ToArray(),
            },
        },
    };

    private static DateTimeOffset ApplyJitter(ScheduleDefinition definition, DateTimeOffset scheduled)
    {
        if (definition.MaximumJitterSeconds == 0)
        {
            return scheduled;
        }

        var bytes = Encoding.UTF8.GetBytes($"{definition.Id}:{scheduled.UtcTicks}");
        var value = BitConverter.ToUInt32(SHA256.HashData(bytes));
        return scheduled.AddSeconds(value % (definition.MaximumJitterSeconds + 1));
    }

    private static string OccurrenceHash(ScheduleId id, DateTimeOffset scheduled, string source) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{id}:{scheduled.UtcTicks}:{source}")))}";

    private static bool IsHash(string? value) => value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept(LowerHex) < 0;

    private static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => !char.IsControl(character));

    private static string ComputeHash(ScheduleSnapshot snapshot)
    {
        var builder = new StringBuilder(4096);
        Append(builder, snapshot.Definition.Id);
        Append(builder, snapshot.Definition.InstallationId);
        Append(builder, snapshot.Definition.AgentId);
        Append(builder, snapshot.Definition.AgentVersion);
        Append(builder, snapshot.Definition.Trigger.Kind);
        Append(builder, snapshot.Definition.Trigger.OneShotAt?.UtcTicks ?? 0);
        Append(builder, snapshot.Definition.Trigger.IntervalAnchor?.UtcTicks ?? 0);
        Append(builder, snapshot.Definition.Trigger.IntervalSeconds ?? 0);
        Append(builder, snapshot.Definition.Trigger.CronExpression ?? string.Empty);
        Append(builder, snapshot.Definition.Trigger.Calendar?.Hour ?? -1);
        Append(builder, snapshot.Definition.Trigger.Calendar?.Minute ?? -1);
        foreach (var day in snapshot.Definition.Trigger.Calendar?.DaysOfWeek ?? [])
        {
            Append(builder, day);
        }

        Append(builder, snapshot.Definition.Trigger.Calendar?.DayOfMonth ?? 0);
        Append(builder, snapshot.Definition.TimeZoneId);
        Append(builder, snapshot.Definition.MisfirePolicy);
        Append(builder, snapshot.Definition.OverlapPolicy);
        Append(builder, snapshot.Definition.MisfireGraceSeconds);
        Append(builder, snapshot.Definition.MaximumCatchUp);
        Append(builder, snapshot.Definition.MaximumParallelRuns);
        Append(builder, snapshot.Definition.MaximumJitterSeconds);
        Append(builder, snapshot.Definition.MaximumAttempts);
        Append(builder, snapshot.Definition.RetryDelaySeconds);
        Append(builder, snapshot.Definition.MaximumConsecutiveFailures);
        Append(builder, snapshot.Definition.ExpiresAt?.UtcTicks ?? 0);
        Append(builder, snapshot.Definition.PolicySnapshotHash);
        Append(builder, snapshot.Definition.CapabilitySnapshotHash);
        Append(builder, snapshot.Definition.BudgetSnapshotHash);
        Append(builder, snapshot.Definition.SkillSnapshotHash);
        Append(builder, snapshot.Version);
        Append(builder, snapshot.State);
        Append(builder, snapshot.NextScheduledFor?.UtcTicks ?? 0);
        Append(builder, snapshot.NextDueAt?.UtcTicks ?? 0);
        foreach (var occurrence in snapshot.Occurrences)
        {
            Append(builder, occurrence.IdempotencyKeyHash);
            Append(builder, occurrence.ScheduledFor.UtcTicks);
            Append(builder, occurrence.DueAt.UtcTicks);
            Append(builder, occurrence.State);
            Append(builder, occurrence.Attempt);
            Append(builder, occurrence.RetryNotBefore?.UtcTicks ?? 0);
            Append(builder, occurrence.Lease?.Owner ?? string.Empty);
            Append(builder, occurrence.Lease?.TokenHash ?? string.Empty);
            Append(builder, occurrence.Lease?.AcquiredAt.UtcTicks ?? 0);
            Append(builder, occurrence.Lease?.ExpiresAt.UtcTicks ?? 0);
            Append(builder, occurrence.EvidenceHash);
            Append(builder, occurrence.IsRunNow);
        }

        Append(builder, snapshot.CompletedCount);
        Append(builder, snapshot.FailedCount);
        Append(builder, snapshot.SkippedCount);
        Append(builder, snapshot.ConsecutiveFailures);
        Append(builder, snapshot.LastOccurrenceIdHash ?? string.Empty);
        Append(builder, snapshot.LastEvidenceHash);
        Append(builder, snapshot.PreviousSnapshotHash);
        Append(builder, snapshot.CreatedAt.UtcTicks);
        Append(builder, snapshot.UpdatedAt.UtcTicks);
        Append(builder, snapshot.ActorId);
        Append(builder, snapshot.IdempotencyKey);
        Append(builder, snapshot.CorrelationId);
        Append(builder, snapshot.CausationId?.Value ?? string.Empty);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))}";
    }

    private static void Append(StringBuilder builder, object value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        builder.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append(';');
    }

    private static DomainResult<ScheduleSnapshot> Invalid(string message) =>
        DomainResult.Fail<ScheduleSnapshot>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<ScheduleSnapshot> Conflict(string message) =>
        DomainResult.Fail<ScheduleSnapshot>(new DomainFailure(FailureCode.ConcurrencyConflict, message));
}

public static class ScheduleCalculator
{
    public static DomainResult<IReadOnlyList<DateTimeOffset>> Preview(
        ScheduleDefinition definition,
        DateTimeOffset after,
        int count,
        TimeZoneInfo timeZone)
    {
        if (count is < 1 or > 128 || !string.Equals(definition.TimeZoneId, timeZone.Id, StringComparison.Ordinal) ||
            !IsValid(definition.Trigger))
        {
            return DomainResult.Fail<IReadOnlyList<DateTimeOffset>>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Schedule preview requires a valid trigger, timezone, and bounded count."));
        }

        var result = new List<DateTimeOffset>(count);
        DateTimeOffset? previous = null;
        var cursor = after;
        while (result.Count < count)
        {
            var next = Next(definition, previous, cursor, timeZone);
            if (!next.IsSuccess)
            {
                return DomainResult.Fail<IReadOnlyList<DateTimeOffset>>(next.Failure!);
            }

            if (next.Value is null || definition.ExpiresAt is { } expires && next.Value > expires)
            {
                break;
            }

            result.Add(next.Value.Value);
            previous = next.Value;
            cursor = next.Value.Value;
        }

        return DomainResult.Success<IReadOnlyList<DateTimeOffset>>(result);
    }

    public static DomainResult<DateTimeOffset?> Next(
        ScheduleDefinition definition,
        DateTimeOffset? previous,
        DateTimeOffset after,
        TimeZoneInfo timeZone)
    {
        if (!IsValid(definition.Trigger) ||
            !string.Equals(definition.TimeZoneId, timeZone.Id, StringComparison.Ordinal))
        {
            return DomainResult.Fail<DateTimeOffset?>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Schedule recurrence and timezone are invalid."));
        }

        return definition.Trigger.Kind switch
        {
            ScheduleTriggerKind.OneShot => DomainResult.Success<DateTimeOffset?>(
                previous is null && definition.Trigger.OneShotAt > after
                    ? definition.Trigger.OneShotAt
                    : null),
            ScheduleTriggerKind.Interval => DomainResult.Success<DateTimeOffset?>(
                NextInterval(definition.Trigger.IntervalAnchor!.Value,
                    definition.Trigger.IntervalSeconds!.Value, previous, after)),
            ScheduleTriggerKind.Cron => NextLocal(definition.Trigger, previous, after, timeZone, cron: true),
            ScheduleTriggerKind.Calendar => NextLocal(definition.Trigger, previous, after, timeZone, cron: false),
            _ => DomainResult.Fail<DateTimeOffset?>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Schedule trigger kind is unsupported.")),
        };
    }

    public static bool IsValid(ScheduleTrigger? trigger)
    {
        if (trigger is null || !Enum.IsDefined(trigger.Kind))
        {
            return false;
        }

        return trigger.Kind switch
        {
            ScheduleTriggerKind.OneShot => trigger.OneShotAt is not null &&
                trigger.IntervalAnchor is null && trigger.IntervalSeconds is null &&
                trigger.CronExpression is null && trigger.Calendar is null,
            ScheduleTriggerKind.Interval => trigger.IntervalAnchor is not null &&
                trigger.IntervalSeconds is >= 1 and <= 31_536_000 && trigger.OneShotAt is null &&
                trigger.CronExpression is null && trigger.Calendar is null,
            ScheduleTriggerKind.Cron => CronFields.TryParse(trigger.CronExpression, out _) &&
                trigger.OneShotAt is null && trigger.IntervalAnchor is null &&
                trigger.IntervalSeconds is null && trigger.Calendar is null,
            ScheduleTriggerKind.Calendar => IsValid(trigger.Calendar) && trigger.OneShotAt is null &&
                trigger.IntervalAnchor is null && trigger.IntervalSeconds is null && trigger.CronExpression is null,
            _ => false,
        };
    }

    private static DateTimeOffset NextInterval(
        DateTimeOffset anchor,
        int seconds,
        DateTimeOffset? previous,
        DateTimeOffset after)
    {
        if (previous is not null)
        {
            return previous.Value.AddSeconds(seconds);
        }

        if (anchor > after)
        {
            return anchor;
        }

        var elapsed = (after.UtcTicks - anchor.UtcTicks) / TimeSpan.TicksPerSecond;
        var steps = elapsed / seconds + 1;
        return anchor.AddSeconds(checked(steps * (long)seconds));
    }

    private static DomainResult<DateTimeOffset?> NextLocal(
        ScheduleTrigger trigger,
        DateTimeOffset? previous,
        DateTimeOffset after,
        TimeZoneInfo timeZone,
        bool cron)
    {
        CronFields? fields = null;
        if (cron && !CronFields.TryParse(trigger.CronExpression, out fields))
        {
            return DomainResult.Fail<DateTimeOffset?>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Cron expression is invalid."));
        }

        var reference = previous ?? after;
        var local = TimeZoneInfo.ConvertTime(reference, timeZone).DateTime;
        local = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0,
            DateTimeKind.Unspecified).AddMinutes(1);
        var limit = local.AddYears(5);
        while (local <= limit)
        {
            var matches = cron ? fields!.Matches(local) : Matches(trigger.Calendar!, local);
            if (matches)
            {
                var resolved = ResolveLocal(local, timeZone);
                if (resolved > reference)
                {
                    return DomainResult.Success<DateTimeOffset?>(resolved);
                }
            }

            local = local.AddMinutes(1);
        }

        return DomainResult.Fail<DateTimeOffset?>(new DomainFailure(
            FailureCode.BudgetExceeded,
            "No local schedule occurrence was found within the bounded five-year scan."));
    }

    private static DateTimeOffset ResolveLocal(DateTime local, TimeZoneInfo timeZone)
    {
        for (var index = 0; index < 180 && timeZone.IsInvalidTime(local); index++)
        {
            local = local.AddMinutes(1);
        }

        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static bool Matches(CalendarScheduleRule rule, DateTime local) =>
        local.Hour == rule.Hour && local.Minute == rule.Minute &&
        (rule.DayOfMonth is { } day ? local.Day == day : rule.DaysOfWeek.Contains(local.DayOfWeek));

    private static bool IsValid(CalendarScheduleRule? rule) => rule is not null &&
        rule.Hour is >= 0 and <= 23 && rule.Minute is >= 0 and <= 59 &&
        rule.DaysOfWeek is not null && rule.DaysOfWeek.Count <= 7 &&
        rule.DaysOfWeek.Distinct().Count() == rule.DaysOfWeek.Count &&
        rule.DaysOfWeek.All(Enum.IsDefined) && rule.DayOfMonth is null or >= 1 and <= 31 &&
        (rule.DayOfMonth is not null ^ rule.DaysOfWeek.Count > 0);

    private sealed record CronFields(
        HashSet<int> Minutes,
        HashSet<int> Hours,
        HashSet<int> Days,
        HashSet<int> Months,
        HashSet<int> DaysOfWeek)
    {
        public bool Matches(DateTime local) => Minutes.Contains(local.Minute) && Hours.Contains(local.Hour) &&
            Days.Contains(local.Day) && Months.Contains(local.Month) && DaysOfWeek.Contains((int)local.DayOfWeek);

        public static bool TryParse(string? expression, out CronFields? result)
        {
            result = null;
            var parts = expression?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts is not { Length: 5 } || !TryField(parts[0], 0, 59, out var minutes) ||
                !TryField(parts[1], 0, 23, out var hours) || !TryField(parts[2], 1, 31, out var days) ||
                !TryField(parts[3], 1, 12, out var months) || !TryField(parts[4], 0, 6, out var daysOfWeek))
            {
                return false;
            }

            result = new CronFields(minutes, hours, days, months, daysOfWeek);
            return true;
        }

        private static bool TryField(string value, int minimum, int maximum, out HashSet<int> values)
        {
            values = [];
            if (value == "*")
            {
                values.UnionWith(Enumerable.Range(minimum, maximum - minimum + 1));
                return true;
            }

            foreach (var component in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var bounds = component.Split('-', StringSplitOptions.RemoveEmptyEntries);
                if (bounds.Length is < 1 or > 2 || !int.TryParse(bounds[0], CultureInfo.InvariantCulture, out var start) ||
                    (bounds.Length == 2 && !int.TryParse(bounds[1], CultureInfo.InvariantCulture, out _)))
                {
                    return false;
                }

                var end = bounds.Length == 1
                    ? start
                    : int.Parse(bounds[1], CultureInfo.InvariantCulture);
                if (start < minimum || end > maximum || start > end)
                {
                    return false;
                }

                values.UnionWith(Enumerable.Range(start, end - start + 1));
            }

            return values.Count > 0;
        }
    }
}
