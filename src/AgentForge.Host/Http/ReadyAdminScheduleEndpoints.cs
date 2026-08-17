using System.Collections.Immutable;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Scheduling;
using AgentForge.Abstractions.Skills;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Scheduling;
using AgentForge.Domain.Skills;

namespace AgentForge.Host.Http;

internal sealed record ReadyScheduleCreateWebRequest(
    long ExpectedInstallationVersion,
    long ExpectedAgentVersion,
    long ExpectedProviderVersion,
    Guid AgentId,
    string Name,
    string Prompt,
    string? RunInstructions,
    string? ResponseDepth,
    int? MaximumOutputTokens,
    IReadOnlyList<string>? SkillIds,
    string TriggerKind,
    DateTimeOffset? OneShotAt,
    int? IntervalSeconds,
    string? CronExpression,
    int? CalendarHour,
    int? CalendarMinute,
    IReadOnlyList<string>? CalendarDays,
    int? CalendarDayOfMonth,
    string TimeZoneId,
    string MisfirePolicy,
    string OverlapPolicy,
    int MisfireGraceSeconds,
    int MaximumCatchUp,
    int MaximumParallelRuns,
    int MaximumJitterSeconds,
    int MaximumAttempts,
    int RetryDelaySeconds,
    int MaximumConsecutiveFailures,
    DateTimeOffset? ExpiresAt);

internal sealed record ReadyScheduleApplyWebRequest(
    string PreviewHash,
    ReadyScheduleCreateWebRequest Schedule);

internal sealed record ReadyScheduleMutationWebRequest(long ExpectedVersion);

internal static partial class ReadyAdminEndpoints
{
    private static async Task<IResult> ListSchedulesAsync(
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IScheduleSnapshotStore schedules,
        IScheduledAgentRunStore templates,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireAsync(
            context, sessions, stateReader, clock, requireCsrf: false, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;

        var items = await schedules.ListLatestAsync(
            acquired.Session!.InstallationId, 100, cancellationToken);
        var responses = new List<object>(items.Count);
        foreach (var item in items)
        {
            var runTemplate = await templates.FindAsync(item.Definition.Id, cancellationToken);
            responses.Add(ScheduleResponse(item, runTemplate));
        }
        return Results.Ok(new { schedules = responses, correlationId = context.TraceIdentifier });
    }

    private static async Task<IResult> PreviewScheduleCreateAsync(
        ReadyScheduleCreateWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IAgentIdentityRepository agents,
        IProviderProfileRepository providers,
        ISkillRegistryRepository skillRegistry,
        IScheduleService schedules,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        var session = acquired.Session!;
        var installation = await stateReader.ReadAsync(cancellationToken);
        if (installation.Version != request.ExpectedInstallationVersion)
        {
            return Problem(context, 409, "Installation changed",
                "Refresh the exact installation, agent, and provider versions before previewing this schedule.",
                "concurrency-conflict");
        }
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var stable = StableRequestIdentity(session.InstallationId, "schedule-preview", idempotencyKey);
        var scheduleId = new ScheduleId(new Guid(stable.AsSpan(0, 16)));
        var prepared = await PrepareScheduleAsync(
            request, scheduleId, session, agents, providers, skillRegistry, clock, cancellationToken);
        if (!prepared.IsSuccess)
        {
            return DomainProblem(context, prepared.Failure!, "Schedule preview failed");
        }

        var requestHash = SnapshotHash(new
        {
            Kind = "ready-scheduled-agent-run-v1",
            ScheduleId = scheduleId.Value,
            Request = request,
            prepared.Value.Agent.Version,
            ProviderVersion = prepared.Value.Provider.Version,
            prepared.Value.SkillAuthorityHash,
        });
        var scopedKey = $"schedule-preview:{idempotencyKey}";
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another schedule preview.", "idempotency-conflict");
            }

            var preview = schedules.Preview(prepared.Value.Definition, clock.UtcNow, 5);
            if (!preview.IsSuccess)
            {
                return DomainProblem(context, preview.Failure!, "Schedule recurrence is invalid");
            }
            var correlation = new CorrelationId($"admin-schedule:{Convert.ToHexStringLower(stable)}");
            RetainBoundedPreviews(session.ScheduleCreatePreviews, 7);
            session.ScheduleCreatePreviews[requestHash] = new ReadyScheduleCreatePreview(
                scheduleId,
                request.ExpectedInstallationVersion,
                request.ExpectedAgentVersion,
                request.ExpectedProviderVersion,
                requestHash,
                correlation);
            var response = new
            {
                previewHash = requestHash,
                scheduleId = scheduleId.Value,
                agent = new { id = prepared.Value.Agent.Id.Value, prepared.Value.Agent.Name, prepared.Value.Agent.Version },
                provider = new { id = prepared.Value.Provider.Id.Value, prepared.Value.Provider.Name, prepared.Value.Provider.Model, prepared.Value.Provider.Version },
                trigger = prepared.Value.Definition.Trigger,
                prepared.Value.Definition.TimeZoneId,
                misfirePolicy = prepared.Value.Definition.MisfirePolicy.ToString(),
                overlapPolicy = prepared.Value.Definition.OverlapPolicy.ToString(),
                nextOccurrences = preview.Value,
                skills = prepared.Value.Skills.Select(item => new
                {
                    id = item.Package.Id.Value,
                    version = item.Package.Version.Value,
                    item.Package.PackageHash,
                }),
                warning = "The schedule pins this exact authority. Agent or provider edits require a replacement schedule; they never silently change it.",
                correlationId = correlation.Value,
            };
            StoreIdempotentResult(session, scopedKey, requestHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static async Task<IResult> ApplyScheduleCreateAsync(
        ReadyScheduleApplyWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IAgentIdentityRepository agents,
        IProviderProfileRepository providers,
        ISkillRegistryRepository skillRegistry,
        ISkillSnapshotService skillSnapshots,
        IScheduledAgentRunService scheduledRuns,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        var session = acquired.Session!;
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"schedule-apply:{idempotencyKey}";
        var applyHash = SnapshotHash(new { request.PreviewHash, Request = request.Schedule });
        if (session.Results.TryGetValue(scopedKey, out var replay))
        {
            return string.Equals(replay.RequestHash, applyHash, StringComparison.Ordinal)
                ? Replay(context, replay.Response)
                : Problem(context, 409, "Idempotency conflict",
                    "The idempotency key is already bound to another schedule creation.", "idempotency-conflict");
        }
        if (!Text(request.PreviewHash, 128) ||
            !session.ScheduleCreatePreviews.TryGetValue(request.PreviewHash, out var approved))
        {
            return Problem(context, 403, "Approved schedule preview required",
                "Preview the exact schedule and run template in this session before applying it.", "policydenied");
        }
        var installation = await stateReader.ReadAsync(cancellationToken);
        if (installation.Version != approved.ExpectedInstallationVersion)
        {
            return Problem(context, 409, "Installation changed",
                "The approved installation version changed; preview a replacement schedule.",
                "concurrency-conflict");
        }

        var prepared = await PrepareScheduleAsync(
            request.Schedule, approved.ScheduleId, session, agents, providers, skillRegistry, clock, cancellationToken);
        if (!prepared.IsSuccess)
        {
            return DomainProblem(context, prepared.Failure!, "Schedule creation failed");
        }
        var recomputed = SnapshotHash(new
        {
            Kind = "ready-scheduled-agent-run-v1",
            ScheduleId = approved.ScheduleId.Value,
            Request = request.Schedule,
            prepared.Value.Agent.Version,
            ProviderVersion = prepared.Value.Provider.Version,
            prepared.Value.SkillAuthorityHash,
        });
        if (!string.Equals(recomputed, approved.RequestHash, StringComparison.Ordinal) ||
            request.Schedule.ExpectedInstallationVersion != approved.ExpectedInstallationVersion ||
            request.Schedule.ExpectedAgentVersion != approved.ExpectedAgentVersion ||
            request.Schedule.ExpectedProviderVersion != approved.ExpectedProviderVersion)
        {
            return Problem(context, 403, "Schedule preview changed",
                "The recurrence, run content, skills, or authority no longer match the approved preview.", "policydenied");
        }

        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, applyHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another schedule creation.", "idempotency-conflict");
            }

            var skillBodies = new List<RunSkillBody>(prepared.Value.Skills.Count);
            var appliedSkillIds = Array.Empty<string>();
            var skillSnapshotHash = prepared.Value.Definition.SkillSnapshotHash;
            if (prepared.Value.Skills.Count > 0)
            {
                var selectedSkillIds = prepared.Value.Skills
                    .Select(item => item.Package.Id)
                    .OrderBy(item => item.Value, StringComparer.Ordinal)
                    .ToArray();
                var snapshot = await skillSnapshots.CreateAsync(
                    new SkillRunSnapshotId(approved.ScheduleId.Value),
                    session.InstallationId,
                    selectedSkillIds,
                    session.ActorId,
                    StoredIdempotencyKey("schedule-skills", approved.RequestHash),
                    approved.CorrelationId,
                    null,
                    cancellationToken);
                if (!snapshot.IsSuccess)
                {
                    return DomainProblem(context, snapshot.Failure!, "Scheduled skill snapshot failed");
                }

                appliedSkillIds = snapshot.Value.Selections.Select(item => item.SkillId.Value)
                    .Order(StringComparer.Ordinal).ToArray();
                skillSnapshotHash = snapshot.Value.SnapshotHash;
                foreach (var selection in snapshot.Value.Selections.OrderBy(
                    item => item.SkillId.Value, StringComparer.Ordinal))
                {
                    var body = await skillSnapshots.OpenBodyAsync(
                        snapshot.Value.Id, selection.SkillId, cancellationToken);
                    if (!body.IsSuccess)
                    {
                        return DomainProblem(context, body.Failure!, "Scheduled skill content failed integrity validation");
                    }
                    skillBodies.Add(new RunSkillBody(
                        selection.SkillId.Value,
                        selection.Version.Value,
                        selection.PackageHash,
                        body.Value));
                }
            }

            var runInstructions = string.IsNullOrWhiteSpace(request.Schedule.RunInstructions)
                ? null
                : request.Schedule.RunInstructions.Trim();
            var systemInstruction = BuildSystemInstruction(prepared.Value.Agent, runInstructions, skillBodies);
            var definition = prepared.Value.Definition with { SkillSnapshotHash = skillSnapshotHash };
            var creation = await scheduledRuns.CreateAsync(new CreateScheduledAgentRunRequest(
                definition,
                prepared.Value.Provider.Id,
                prepared.Value.Provider.Version,
                prepared.Value.Provider.Model,
                request.Schedule.Name.Trim(),
                systemInstruction,
                request.Schedule.Prompt,
                appliedSkillIds,
                skillSnapshotHash,
                prepared.Value.MaximumOutputTokens,
                prepared.Value.MaximumWallClockSeconds,
                session.ActorId,
                StoredIdempotencyKey("schedule-create", approved.RequestHash),
                approved.CorrelationId,
                null), cancellationToken);
            if (!creation.IsSuccess)
            {
                return DomainProblem(context, creation.Failure!, "Schedule creation failed");
            }

            var response = new
            {
                schedule = ScheduleResponse(creation.Value.Schedule, creation.Value.Template),
                nextOccurrences = creation.Value.Preview,
                previewHash = approved.RequestHash,
                correlationId = approved.CorrelationId.Value,
            };
            session.ScheduleCreatePreviews.TryRemove(request.PreviewHash, out _);
            StoreIdempotentResult(session, scopedKey, applyHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static Task<IResult> PauseScheduleAsync(
        Guid scheduleId,
        ReadyScheduleMutationWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IScheduleSnapshotStore snapshots,
        IScheduleService schedules,
        IClock clock,
        CancellationToken cancellationToken) => MutateScheduleAsync(
            "pause", scheduleId, request, context, sessions, stateReader, snapshots, schedules, clock,
            (service, id, version, _, token) => service.PauseAsync(id, version, token), cancellationToken);

    private static Task<IResult> ResumeScheduleAsync(
        Guid scheduleId,
        ReadyScheduleMutationWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IScheduleSnapshotStore snapshots,
        IScheduleService schedules,
        IClock clock,
        CancellationToken cancellationToken) => MutateScheduleAsync(
            "resume", scheduleId, request, context, sessions, stateReader, snapshots, schedules, clock,
            (service, id, version, _, token) => service.ResumeAsync(id, version, token), cancellationToken);

    private static Task<IResult> RunScheduleNowAsync(
        Guid scheduleId,
        ReadyScheduleMutationWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IScheduleSnapshotStore snapshots,
        IScheduleService schedules,
        IClock clock,
        CancellationToken cancellationToken) => MutateScheduleAsync(
            "run-now", scheduleId, request, context, sessions, stateReader, snapshots, schedules, clock,
            (service, id, version, key, token) => service.RunNowAsync(id, version, key, token), cancellationToken);

    private static async Task<IResult> MutateScheduleAsync(
        string operationName,
        Guid scheduleId,
        ReadyScheduleMutationWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IScheduleSnapshotStore snapshots,
        IScheduleService schedules,
        IClock clock,
        Func<IScheduleService, ScheduleId, long, string, CancellationToken,
            Task<DomainResult<ScheduleTransitionResult>>> operation,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        var session = acquired.Session!;
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"schedule-{operationName}:{scheduleId:D}:{idempotencyKey}";
        var requestHash = SnapshotHash(new { operationName, scheduleId, request.ExpectedVersion });
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another schedule mutation.",
                        "idempotency-conflict");
            }
            var id = new ScheduleId(scheduleId);
            var current = await snapshots.FindLatestAsync(id, cancellationToken);
            if (current is null || current.Definition.InstallationId != session.InstallationId)
            {
                return Problem(context, 404, "Schedule not found",
                    "The requested schedule does not belong to this installation.", "not-found");
            }
            var result = await operation(
                schedules,
                id,
                request.ExpectedVersion,
                StoredIdempotencyKey($"schedule-{operationName}", idempotencyKey),
                cancellationToken);
            if (!result.IsSuccess)
            {
                return DomainProblem(context, result.Failure!, "Schedule mutation failed");
            }
            var response = new
            {
                schedule = ScheduleResponse(result.Value.Snapshot, null),
                correlationId = context.TraceIdentifier,
            };
            StoreIdempotentResult(session, scopedKey, requestHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static async Task<DomainResult<PreparedSchedule>> PrepareScheduleAsync(
        ReadyScheduleCreateWebRequest request,
        ScheduleId scheduleId,
        ReadyAdminSession session,
        IAgentIdentityRepository agents,
        IProviderProfileRepository providers,
        ISkillRegistryRepository skills,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (request is null || scheduleId.Value == Guid.Empty || request.AgentId == Guid.Empty ||
            !Text(request.Name?.Trim(), 120) || !PromptText(request.Prompt, 16_384) ||
            !Text(request.TimeZoneId?.Trim(), 128) ||
            !TryEnum(request.MisfirePolicy, out ScheduleMisfirePolicy misfire) ||
            !TryEnum(request.OverlapPolicy, out ScheduleOverlapPolicy overlap))
        {
            return InvalidPrepared("Schedule identity, content, timezone, or policies are invalid.");
        }
        var agent = await agents.FindByIdAsync(new AgentIdentityId(request.AgentId), cancellationToken);
        if (agent is null || agent.InstallationId != session.InstallationId ||
            agent.Version != request.ExpectedAgentVersion ||
            agent.ModelPolicy.DataLocality is not ModelDataLocality.LocalOnly || agent.ModelPolicy.AllowFallback)
        {
            return DeniedPrepared("Scheduled runs require the exact current local-only, no-fallback agent version.");
        }
        var provider = await providers.FindByIdAsync(agent.ModelPolicy.PrimaryProviderProfileId, cancellationToken);
        if (provider is null || provider.InstallationId != session.InstallationId ||
            provider.Version != request.ExpectedProviderVersion)
        {
            return ConflictPrepared("The pinned provider version changed; refresh the schedule preview.");
        }
        var depth = string.IsNullOrWhiteSpace(request.ResponseDepth)
            ? "balanced"
            : request.ResponseDepth.Trim().ToLowerInvariant();
        if (depth is not ("concise" or "balanced" or "detailed" or "extended" or "maximum") ||
            !string.IsNullOrWhiteSpace(request.RunInstructions) &&
                !PromptText(request.RunInstructions.Trim(), 2_048))
        {
            return InvalidPrepared("Scheduled response depth or run guidance is invalid.");
        }
        var ceiling = (int)Math.Clamp(agent.Budget.MaxOutputTokens, 1L, MaximumInteractiveOutputTokens);
        if (request.MaximumOutputTokens is { } selected && (selected < 1 || selected > ceiling))
        {
            return DomainResult.Fail<PreparedSchedule>(new DomainFailure(
                FailureCode.BudgetExceeded,
                $"Choose an output-token limit between 1 and the agent ceiling of {ceiling:N0}."));
        }
        var maximumOutputTokens = request.MaximumOutputTokens ?? ResponseTokenLimit(depth, agent.Budget.MaxOutputTokens);
        var maximumWallClockSeconds = Math.Clamp(
            agent.Budget.MaxWallClockSeconds, 1, MaximumInteractiveWallClockSeconds);
        var selectedSkills = (request.SkillIds ?? []).Select(item => item.Trim())
            .Order(StringComparer.Ordinal).ToArray();
        if (selectedSkills.Length > 4 || selectedSkills.Distinct(StringComparer.Ordinal).Count() != selectedSkills.Length ||
            selectedSkills.Any(item => !SkillIdText(item) ||
                !agent.CapabilityPolicy.SkillGrants.Contains(item, StringComparer.Ordinal)))
        {
            return DeniedPrepared("Every scheduled skill must be distinct, bounded, and granted to this agent version.");
        }
        var active = new List<RegisteredSkillVersion>(selectedSkills.Length);
        foreach (var skillId in selectedSkills)
        {
            var item = await skills.FindActiveAsync(
                session.InstallationId, new SkillId(skillId), cancellationToken);
            if (item is null)
            {
                return DeniedPrepared($"Scheduled skill '{skillId}' is not Active.");
            }
            active.Add(item);
        }

        var trigger = Trigger(request, clock.UtcNow);
        if (!trigger.IsSuccess) return DomainResult.Fail<PreparedSchedule>(trigger.Failure!);
        var definition = new ScheduleDefinition(
            scheduleId,
            session.InstallationId,
            agent.Id,
            agent.Version,
            trigger.Value,
            request.TimeZoneId!.Trim(),
            misfire,
            overlap,
            request.MisfireGraceSeconds,
            request.MaximumCatchUp,
            request.MaximumParallelRuns,
            request.MaximumJitterSeconds,
            request.MaximumAttempts,
            request.RetryDelaySeconds,
            request.MaximumConsecutiveFailures,
            request.ExpiresAt,
            SnapshotHash(new { agent.CapabilityPolicy, agent.Version, ProviderVersion = provider.Version }),
            SnapshotHash(new { agent.CapabilityPolicy, agent.Version }),
            SnapshotHash(new
            {
                agent.Budget,
                agent.ChildLimits,
                agent.Version,
                maximumOutputTokens,
                maximumWallClockSeconds,
            }),
            SnapshotHash(active.Select(item => new
            {
                Id = item.Package.Id.Value,
                Version = item.Package.Version.Value,
                item.Package.PackageHash,
            })));
        return DomainResult.Success(new PreparedSchedule(
            definition,
            agent,
            provider,
            active,
            definition.SkillSnapshotHash,
            maximumOutputTokens,
            maximumWallClockSeconds));
    }

    private static DomainResult<ScheduleTrigger> Trigger(
        ReadyScheduleCreateWebRequest request,
        DateTimeOffset now)
    {
        if (!TryEnum(request.TriggerKind, out ScheduleTriggerKind kind))
        {
            return DomainResult.Fail<ScheduleTrigger>(new DomainFailure(
                FailureCode.ValidationFailure, "Schedule trigger kind is invalid."));
        }
        return kind switch
        {
            ScheduleTriggerKind.OneShot when request.OneShotAt is { } at && at > now =>
                DomainResult.Success(new ScheduleTrigger(kind, at, null, null, null, null)),
            ScheduleTriggerKind.Interval when request.IntervalSeconds is >= 1 and <= 31_536_000 =>
                DomainResult.Success(new ScheduleTrigger(kind, null, now, request.IntervalSeconds, null, null)),
            ScheduleTriggerKind.Cron when Text(request.CronExpression?.Trim(), 128) =>
                DomainResult.Success(new ScheduleTrigger(kind, null, null, null, request.CronExpression!.Trim(), null)),
            ScheduleTriggerKind.Calendar when request.CalendarHour is >= 0 and <= 23 &&
                request.CalendarMinute is >= 0 and <= 59 && CalendarDays(request.CalendarDays).IsSuccess =>
                DomainResult.Success(new ScheduleTrigger(
                    kind,
                    null,
                    null,
                    null,
                    null,
                    new CalendarScheduleRule(
                        request.CalendarHour.Value,
                        request.CalendarMinute.Value,
                        CalendarDays(request.CalendarDays).Value,
                        request.CalendarDayOfMonth))),
            _ => DomainResult.Fail<ScheduleTrigger>(new DomainFailure(
                FailureCode.ValidationFailure, "Schedule recurrence fields are incomplete or out of bounds.")),
        };
    }

    private static DomainResult<IReadOnlyList<DayOfWeek>> CalendarDays(IReadOnlyList<string>? values)
    {
        values ??= [];
        var days = new List<DayOfWeek>(values.Count);
        foreach (var value in values)
        {
            if (!TryEnum(value, out DayOfWeek day))
            {
                return DomainResult.Fail<IReadOnlyList<DayOfWeek>>(new DomainFailure(
                    FailureCode.ValidationFailure, "Calendar day selection is invalid."));
            }
            days.Add(day);
        }
        return days.Distinct().Count() == days.Count
            ? DomainResult.Success<IReadOnlyList<DayOfWeek>>(days)
            : DomainResult.Fail<IReadOnlyList<DayOfWeek>>(new DomainFailure(
                FailureCode.ValidationFailure, "Calendar days must be distinct."));
    }

    private static object ScheduleResponse(
        ScheduleSnapshot snapshot,
        ScheduledAgentRunTemplate? runTemplate) => new
        {
            id = snapshot.Definition.Id.Value,
            agentId = snapshot.Definition.AgentId.Value,
            agentVersion = snapshot.Definition.AgentVersion,
            name = runTemplate?.Name ?? "Legacy schedule",
            trigger = snapshot.Definition.Trigger,
            snapshot.Definition.TimeZoneId,
            misfirePolicy = snapshot.Definition.MisfirePolicy.ToString(),
            overlapPolicy = snapshot.Definition.OverlapPolicy.ToString(),
            state = snapshot.State.ToString(),
            snapshot.NextScheduledFor,
            snapshot.NextDueAt,
            queued = snapshot.Occurrences.Count(item => item.State is ScheduleOccurrenceState.Queued),
            running = snapshot.Occurrences.Count(item => item.State is ScheduleOccurrenceState.Running),
            snapshot.CompletedCount,
            snapshot.FailedCount,
            snapshot.SkippedCount,
            snapshot.ConsecutiveFailures,
            snapshot.Version,
            snapshot.SnapshotHash,
            templateHash = runTemplate?.TemplateHash,
            createdAt = snapshot.CreatedAt,
            updatedAt = snapshot.UpdatedAt,
        };

    private static DomainResult<PreparedSchedule> InvalidPrepared(string message) =>
        DomainResult.Fail<PreparedSchedule>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<PreparedSchedule> DeniedPrepared(string message) =>
        DomainResult.Fail<PreparedSchedule>(new DomainFailure(FailureCode.PolicyDenied, message));

    private static DomainResult<PreparedSchedule> ConflictPrepared(string message) =>
        DomainResult.Fail<PreparedSchedule>(new DomainFailure(
            FailureCode.ConcurrencyConflict, message, IsRetryable: true));

    private sealed record PreparedSchedule(
        ScheduleDefinition Definition,
        AgentIdentity Agent,
        Domain.Providers.ProviderProfile Provider,
        IReadOnlyList<RegisteredSkillVersion> Skills,
        string SkillAuthorityHash,
        int MaximumOutputTokens,
        int MaximumWallClockSeconds);
}
