using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Scheduling;

namespace AgentForge.UnitTests;

public sealed class ScheduleStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 3, 0, 0, TimeSpan.Zero);
    private const string HashA = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Token = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void One_shot_interval_cron_and_calendar_previews_are_deterministic()
    {
        var utc = TimeZoneInfo.Utc;
        var oneShot = Definition(new ScheduleTrigger(
            ScheduleTriggerKind.OneShot, Now.AddHours(1), null, null, null, null));
        Assert.Equal([Now.AddHours(1)], Preview(oneShot, Now, 3, utc));

        var interval = Definition(new ScheduleTrigger(
            ScheduleTriggerKind.Interval, null, Now, 900, null, null));
        Assert.Equal(
            [Now.AddMinutes(15), Now.AddMinutes(30), Now.AddMinutes(45)],
            Preview(interval, Now, 3, utc));

        var cron = Definition(new ScheduleTrigger(
            ScheduleTriggerKind.Cron, null, null, null, "0,30 4-5 * * *", null));
        Assert.Equal(
            [Now.AddHours(1), Now.AddHours(1.5), Now.AddHours(2)],
            Preview(cron, Now, 3, utc));

        var calendar = Definition(new ScheduleTrigger(
            ScheduleTriggerKind.Calendar,
            null,
            null,
            null,
            null,
            new CalendarScheduleRule(9, 15, [DayOfWeek.Wednesday], null)));
        Assert.Equal(new DateTimeOffset(2026, 8, 12, 9, 15, 0, TimeSpan.Zero), Preview(calendar, Now, 1, utc)[0]);
    }

    [Fact]
    public void Invalid_and_ambiguous_local_times_use_explicit_cross_platform_policy()
    {
        var zone = CreateEasternFixture();
        var daily = Definition(new ScheduleTrigger(
            ScheduleTriggerKind.Calendar,
            null,
            null,
            null,
            null,
            new CalendarScheduleRule(2, 30, [], 8)), zone.Id);
        var spring = Preview(daily, new DateTimeOffset(2026, 3, 8, 6, 0, 0, TimeSpan.Zero), 1, zone);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), spring[0]);

        var fall = Definition(new ScheduleTrigger(
            ScheduleTriggerKind.Calendar,
            null,
            null,
            null,
            null,
            new CalendarScheduleRule(1, 30, [], 1)), zone.Id);
        var ambiguous = Preview(fall, new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), 1, zone);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), ambiguous[0]);
    }

    [Fact]
    public void Catch_up_is_bounded_and_occurrence_ids_are_stable()
    {
        var definition = Definition(new ScheduleTrigger(
            ScheduleTriggerKind.Interval, null, Now, 60, null, null)) with
        {
            MisfirePolicy = ScheduleMisfirePolicy.CatchUp,
            MaximumCatchUp = 3,
        };
        var current = Create(definition, Now);
        var due = ScheduleStateMachine.EvaluateDue(current, TimeZoneInfo.Utc, Now.AddMinutes(5));

        Assert.True(due.IsSuccess, due.Failure?.Message);
        Assert.Equal(3, due.Value.Occurrences.Count);
        Assert.Equal(3, due.Value.SkippedCount);
        Assert.Equal(Now.AddMinutes(6), due.Value.NextScheduledFor);
        Assert.Equal(3, due.Value.Occurrences.Select(item => item.IdempotencyKeyHash).Distinct().Count());
        Assert.True(ScheduleStateMachine.IsConsistent(due.Value));
    }

    [Fact]
    public void Skip_overlap_advances_recurrence_without_duplicate_dispatch()
    {
        var definition = Definition(new ScheduleTrigger(
            ScheduleTriggerKind.Interval, null, Now, 60, null, null)) with
        {
            OverlapPolicy = ScheduleOverlapPolicy.Skip,
        };
        var current = ScheduleStateMachine.EvaluateDue(Create(definition, Now), TimeZoneInfo.Utc, Now.AddMinutes(1)).Value;
        var occurrence = current.Occurrences[0];
        current = ScheduleStateMachine.Claim(
            current,
            occurrence.IdempotencyKeyHash,
            "worker",
            Token,
            Now.AddMinutes(2),
            Now.AddMinutes(1)).Value;
        current = ScheduleStateMachine.EvaluateDue(current, TimeZoneInfo.Utc, Now.AddMinutes(3)).Value;

        Assert.Single(current.Occurrences);
        Assert.Equal(ScheduleOccurrenceState.Running, current.Occurrences[0].State);
        Assert.Equal(3, current.SkippedCount);
        Assert.Equal(Now.AddMinutes(4), current.NextScheduledFor);
    }

    [Fact]
    public void Queue_and_parallel_overlap_have_independent_claim_limits()
    {
        var queueDefinition = Definition(new ScheduleTrigger(
            ScheduleTriggerKind.Interval, null, Now, 60, null, null)) with
        {
            MisfirePolicy = ScheduleMisfirePolicy.CatchUp,
            MaximumCatchUp = 2,
            OverlapPolicy = ScheduleOverlapPolicy.Queue,
        };
        var queued = ScheduleStateMachine.EvaluateDue(
            Create(queueDefinition, Now), TimeZoneInfo.Utc, Now.AddMinutes(2)).Value;
        var first = Claim(queued, queued.Occurrences[0], Now.AddMinutes(2));
        var blocked = ScheduleStateMachine.Claim(
            first,
            first.Occurrences[1].IdempotencyKeyHash,
            "worker-two",
            HashB,
            Now.AddMinutes(3),
            Now.AddMinutes(2));
        Assert.False(blocked.IsSuccess);

        var parallelDefinition = queueDefinition with
        {
            OverlapPolicy = ScheduleOverlapPolicy.Parallel,
            MaximumParallelRuns = 2,
        };
        var parallel = ScheduleStateMachine.EvaluateDue(
            Create(parallelDefinition, Now), TimeZoneInfo.Utc, Now.AddMinutes(2)).Value;
        parallel = Claim(parallel, parallel.Occurrences[0], Now.AddMinutes(2));
        parallel = ScheduleStateMachine.Claim(
            parallel,
            parallel.Occurrences[1].IdempotencyKeyHash,
            "worker-two",
            HashB,
            Now.AddMinutes(3),
            Now.AddMinutes(2)).Value;
        Assert.Equal(2, parallel.Occurrences.Count(item => item.State is ScheduleOccurrenceState.Running));
    }

    [Fact]
    public void Completion_retry_expired_recovery_and_dead_letter_are_typed()
    {
        var definition = Definition(new ScheduleTrigger(
            ScheduleTriggerKind.Interval, null, Now, 60, null, null)) with
        {
            MaximumAttempts = 2,
            MaximumConsecutiveFailures = 1,
        };
        var current = ScheduleStateMachine.EvaluateDue(Create(definition, Now), TimeZoneInfo.Utc, Now.AddMinutes(1)).Value;
        current = Claim(current, current.Occurrences[0], Now.AddMinutes(1));
        current = ScheduleStateMachine.RecoverExpired(current, Now.AddMinutes(2)).Value;
        Assert.Equal(ScheduleOccurrenceState.Queued, current.Occurrences[0].State);

        current = ScheduleStateMachine.Claim(
            current,
            current.Occurrences[0].IdempotencyKeyHash,
            "worker",
            Token,
            Now.AddMinutes(4),
            Now.AddMinutes(2)).Value;
        current = ScheduleStateMachine.Fail(
            current,
            current.Occurrences[0].IdempotencyKeyHash,
            "worker",
            Token,
            HashB,
            Now.AddMinutes(3)).Value;
        Assert.Equal(ScheduleState.DeadLettered, current.State);
        Assert.Empty(current.Occurrences);
        Assert.Equal(1, current.FailedCount);
    }

    [Fact]
    public void Run_now_pause_resume_expiration_and_tamper_are_bound()
    {
        var definition = Definition(new ScheduleTrigger(
            ScheduleTriggerKind.OneShot, Now.AddMinutes(5), null, null, null, null)) with
        {
            ExpiresAt = Now.AddMinutes(10),
        };
        var current = Create(definition, Now);
        var idempotency = Hash("run-now-1");
        current = ScheduleStateMachine.RunNow(current, idempotency, Now.AddSeconds(1)).Value;
        Assert.False(ScheduleStateMachine.RunNow(current, idempotency, Now.AddSeconds(2)).IsSuccess);
        current = ScheduleStateMachine.Pause(current, Now.AddSeconds(2)).Value;
        Assert.Equal(ScheduleState.Paused, current.State);
        current = ScheduleStateMachine.Resume(current, TimeZoneInfo.Utc, Now.AddMinutes(11)).Value;
        Assert.Equal(ScheduleState.Expired, current.State);
        Assert.False(ScheduleStateMachine.IsConsistent(current with { CompletedCount = 99 }));
    }

    [Fact]
    public void Jitter_is_deterministic_and_never_precedes_base_occurrence()
    {
        var definition = Definition(new ScheduleTrigger(
            ScheduleTriggerKind.Interval, null, Now, 60, null, null)) with
        {
            MaximumJitterSeconds = 30,
        };
        var first = Create(definition, Now);
        var second = Create(definition, Now);
        Assert.Equal(first.NextDueAt, second.NextDueAt);
        Assert.InRange(first.NextDueAt!.Value, first.NextScheduledFor!.Value, first.NextScheduledFor.Value.AddSeconds(30));
    }

    [Theory]
    [InlineData("*/5 * * * *")]
    [InlineData("60 * * * *")]
    [InlineData("0 0 0 * *")]
    [InlineData("0 0 * * 7")]
    public void Unsupported_or_out_of_range_cron_fails_closed(string expression)
    {
        var definition = Definition(new ScheduleTrigger(
            ScheduleTriggerKind.Cron, null, null, null, expression, null));
        Assert.False(ScheduleCalculator.Preview(definition, Now, 1, TimeZoneInfo.Utc).IsSuccess);
    }

    private static IReadOnlyList<DateTimeOffset> Preview(
        ScheduleDefinition definition,
        DateTimeOffset after,
        int count,
        TimeZoneInfo zone)
    {
        var result = ScheduleCalculator.Preview(definition, after, count, zone);
        Assert.True(result.IsSuccess, result.Failure?.Message);
        return result.Value;
    }

    private static ScheduleSnapshot Create(ScheduleDefinition definition, DateTimeOffset now)
    {
        var result = ScheduleStateMachine.Create(
            definition,
            TimeZoneInfo.FindSystemTimeZoneById(definition.TimeZoneId),
            new ActorId("scheduler"),
            "schedule-key",
            new CorrelationId("schedule-correlation"),
            null,
            now);
        Assert.True(result.IsSuccess, result.Failure?.Message);
        return result.Value;
    }

    private static ScheduleSnapshot Claim(
        ScheduleSnapshot current,
        ScheduleOccurrence occurrence,
        DateTimeOffset now) => ScheduleStateMachine.Claim(
        current,
        occurrence.IdempotencyKeyHash,
        "worker",
        Token,
        now.AddMinutes(1),
        now).Value;

    private static ScheduleDefinition Definition(ScheduleTrigger trigger, string? timeZoneId = null) => new(
        new ScheduleId(Guid.Parse("50000000-0000-0000-0000-000000000001")),
        new InstallationId(Guid.Parse("50000000-0000-0000-0000-000000000002")),
        new AgentIdentityId(Guid.Parse("50000000-0000-0000-0000-000000000003")),
        1,
        trigger,
        timeZoneId ?? TimeZoneInfo.Utc.Id,
        ScheduleMisfirePolicy.FireOnce,
        ScheduleOverlapPolicy.Queue,
        30,
        4,
        2,
        0,
        2,
        0,
        3,
        null,
        HashA,
        HashA,
        HashA,
        HashA);

    private static string Hash(string value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";

    private static TimeZoneInfo CreateEasternFixture()
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var adjustment = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2020, 1, 1),
            new DateTime(2030, 12, 31),
            TimeSpan.FromHours(1),
            daylightStart,
            daylightEnd);
        return TimeZoneInfo.CreateCustomTimeZone(
            "AgentForge/EasternFixture",
            TimeSpan.FromHours(-5),
            "AgentForge Eastern fixture",
            "EST",
            "EDT",
            [adjustment]);
    }
}
