using System.Security.Cryptography;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;

namespace AgentForge.Models;

internal sealed class ModelRunExecutionService(
    IModelRunRepository runs,
    IModelBudgetLedgerRepository ledgers,
    IModelProviderHealthRepository health,
    IModelRoutePlanner routePlanner,
    IModelRouteAuthoritySnapshotReader authorityReader,
    IModelProviderCatalog catalog,
    IModelContextPreparer contextPreparer,
    ISensitiveDataRedactor redactor,
    IAuditRecorder auditRecorder,
    IUnitOfWork unitOfWork,
    IClock clock) : IModelRunExecutionService
{
    public async Task<DomainResult<ModelRunExecutionResult>> ExecuteAsync(
        ModelRunExecutionRequest request,
        IModelRunEventObserver? observer,
        CancellationToken cancellationToken)
    {
        var validation = Validate(request);
        if (!validation.IsSuccess)
        {
            return DomainResult.Fail<ModelRunExecutionResult>(validation.Failure!);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (redactor.Redact(new
        {
            ActorId = request.ActorId.Value,
            request.WorkerId,
            CorrelationId = request.CorrelationId.Value,
            CausationId = request.CausationId?.Value,
        }).ContainsRedactions)
        {
            return Invalid("Model run execution metadata cannot contain credential-shaped values.");
        }

        var prepared = contextPreparer.Prepare(request.Request);
        if (!prepared.IsSuccess)
        {
            return DomainResult.Fail<ModelRunExecutionResult>(prepared.Failure!);
        }

        if (prepared.Value.Request.Tools.Count > 0)
        {
            return DomainResult.Fail<ModelRunExecutionResult>(new DomainFailure(
                FailureCode.UnsupportedCapability,
                "Model tool-call execution remains disabled at this runtime boundary."));
        }

        var aggregate = await runs.FindByIdAsync(request.RunId, cancellationToken);
        if (aggregate is null || !RequestMatches(request, aggregate.Run))
        {
            return Conflict("Model run execution does not match the exact reserved request.");
        }

        var currentPlan = await routePlanner.PlanAsync(new ModelRoutePlanningRequest(
            aggregate.Run.InstallationId,
            aggregate.Run.InstallationVersion,
            aggregate.Run.AgentId,
            aggregate.Run.AgentVersion,
            request.Request,
            aggregate.Run.Reservation.InputTokens,
            aggregate.Run.AttemptedProfileIds), cancellationToken);
        if (!currentPlan.IsSuccess)
        {
            return DomainResult.Fail<ModelRunExecutionResult>(currentPlan.Failure!);
        }

        if (!PlanMatchesRun(currentPlan.Value, aggregate.Run))
        {
            return Conflict("Current model route evidence no longer matches the reserved run.");
        }

        var authority = await authorityReader.ReadAsync(
            aggregate.Run.InstallationId,
            aggregate.Run.AgentId,
            cancellationToken);
        if (!authority.IsSuccess)
        {
            return DomainResult.Fail<ModelRunExecutionResult>(authority.Failure!);
        }

        if (authority.Value.Installation.State is not InstallationState.Ready ||
            authority.Value.Installation.Version != aggregate.Run.InstallationVersion ||
            authority.Value.Agent.Version != aggregate.Run.AgentVersion)
        {
            return Conflict("Model run authority changed before the provider start boundary.");
        }

        var resolved = catalog.Resolve(aggregate.Run.Route.ProfileId);
        if (!resolved.IsSuccess || !DescriptorMatches(resolved.IsSuccess ? resolved.Value : null, aggregate.Run))
        {
            return resolved.IsSuccess
                ? Conflict("Resolved model adapter no longer matches the reserved route.")
                : DomainResult.Fail<ModelRunExecutionResult>(resolved.Failure!);
        }

        var currentLedger = await ledgers.FindAsync(aggregate.Run.AgentId, cancellationToken);
        var reservedLedger = ModelBudgetLedgerStateMachine.Reserve(
            currentLedger,
            aggregate.Run,
            authority.Value.Agent.Budget,
            clock.UtcNow);
        if (!reservedLedger.IsSuccess)
        {
            return DomainResult.Fail<ModelRunExecutionResult>(reservedLedger.Failure!);
        }

        var leaseToken = CreateLeaseToken();
        var startedAt = clock.UtcNow;
        var started = ModelRunStateMachine.Start(
            aggregate,
            request.WorkerId,
            leaseToken,
            startedAt,
            startedAt.AddSeconds(aggregate.Run.Reservation.WallClockSeconds + 30L));
        if (!started.IsSuccess)
        {
            return DomainResult.Fail<ModelRunExecutionResult>(started.Failure!);
        }

        await runs.UpdateAsync(
            started.Value,
            aggregate.Run.Version,
            aggregate.Attempt.Version,
            cancellationToken);
        if (reservedLedger.Value.IsNew)
        {
            await ledgers.AddAsync(reservedLedger.Value.Ledger, cancellationToken);
        }
        else
        {
            await ledgers.UpdateAsync(
                reservedLedger.Value.Ledger,
                reservedLedger.Value.ExpectedVersion!.Value,
                cancellationToken);
        }

        await RecordStartedAsync(started.Value, reservedLedger.Value.Ledger, cancellationToken);
        var startCommit = await unitOfWork.CommitAsync(cancellationToken);
        if (!startCommit.Succeeded)
        {
            return DomainResult.Fail<ModelRunExecutionResult>(startCommit.Failure!);
        }

        return await ExecuteProviderAsync(
            started.Value,
            reservedLedger.Value.Ledger,
            leaseToken,
            prepared.Value.Request,
            resolved.Value,
            observer,
            cancellationToken);
    }

    private async Task<DomainResult<ModelRunExecutionResult>> ExecuteProviderAsync(
        ModelRunAggregate running,
        ModelBudgetLedgerRecord startedLedger,
        string leaseToken,
        ModelRequest preparedRequest,
        IModelProvider provider,
        IModelRunEventObserver? observer,
        CancellationToken cancellationToken)
    {
        using var accumulator = new ModelRunEventAccumulator(running.Run);
        using var duration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        duration.CancelAfter(TimeSpan.FromSeconds(running.Run.Reservation.WallClockSeconds));
        DomainFailure? streamFailure = null;
        try
        {
            await foreach (var modelEvent in provider.StreamAsync(preparedRequest, duration.Token)
                .WithCancellation(duration.Token))
            {
                var accepted = accumulator.Accept(modelEvent);
                if (!accepted.IsSuccess)
                {
                    streamFailure = accepted.Failure;
                    break;
                }

                if (observer is not null)
                {
                    await observer.ObserveAsync(modelEvent, duration.Token);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var canceledAt = AtLeast(clock.UtcNow, running.Run.StartedAt!.Value);
            var evidence = accumulator.FinalizeEvidence();
            var canceled = ExceedsReservation(running.Run, accumulator.Usage, evidence, canceledAt)
                ? ModelRunStateMachine.RecordBudgetExceeded(
                    running,
                    leaseToken,
                    accumulator.Usage,
                    evidence,
                    canceledAt)
                : ModelRunStateMachine.CancelRunning(
                    running,
                    leaseToken,
                    accumulator.Usage,
                    evidence,
                    canceledAt);
            if (!canceled.IsSuccess)
            {
                throw new InvalidOperationException(
                    "Canceled model run could not produce valid terminal evidence.");
            }

            var cancellationPersistence = await PersistTerminalAsync(
                canceled.Value,
                startedLedger,
                canceled.Value.Run.State is ModelRunState.Canceled
                    ? AuditOutcome.Canceled
                    : AuditOutcome.Failed,
                canceled.Value.Run.FailureCode?.ToString(),
                CancellationToken.None);
            if (!cancellationPersistence.IsSuccess)
            {
                throw new InvalidOperationException(
                    "Canceled model run could not persist its terminal evidence.");
            }

            throw;
        }
        catch (OperationCanceledException) when (duration.IsCancellationRequested)
        {
            streamFailure = new DomainFailure(
                FailureCode.BudgetExceeded,
                "Model provider attempt exceeded its wall-clock reservation.");
        }
        catch (OperationCanceledException)
        {
            streamFailure = new DomainFailure(
                FailureCode.RecoverableExternalFailure,
                "Model provider attempt was interrupted before a valid terminal event.",
                true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            streamFailure = new DomainFailure(
                FailureCode.RecoverableExternalFailure,
                "Model provider attempt failed before a valid terminal event.",
                true);
        }

        var completedAt = streamFailure?.Code is FailureCode.BudgetExceeded
            ? AtLeast(
                clock.UtcNow,
                running.Run.StartedAt!.Value.AddSeconds(running.Run.Reservation.WallClockSeconds))
            : AtLeast(clock.UtcNow, running.Run.StartedAt!.Value);
        ModelRunStreamEvidence streamEvidence;
        if (streamFailure is null)
        {
            var evidence = accumulator.CompleteEvidence();
            if (!evidence.IsSuccess)
            {
                streamFailure = evidence.Failure;
                streamEvidence = accumulator.FinalizeEvidence();
            }
            else
            {
                streamEvidence = evidence.Value;
            }
        }
        else
        {
            streamEvidence = accumulator.FinalizeEvidence();
        }

        DomainResult<ModelRunAggregate> terminal;
        if (streamFailure?.Code is FailureCode.BudgetExceeded ||
            accumulator.ProviderError?.Code is ModelProviderErrorCode.BudgetExceeded ||
            ExceedsReservation(running.Run, accumulator.Usage, streamEvidence, completedAt))
        {
            terminal = ModelRunStateMachine.RecordBudgetExceeded(
                running,
                leaseToken,
                accumulator.Usage,
                streamEvidence,
                completedAt,
                providerReported: accumulator.ProviderError?.Code is ModelProviderErrorCode.BudgetExceeded);
        }
        else if (streamFailure is not null)
        {
            terminal = ModelRunStateMachine.Fail(
                running,
                leaseToken,
                streamFailure,
                accumulator.Usage,
                streamEvidence,
                completedAt);
        }
        else if (accumulator.ProviderError is { } providerError)
        {
            var failure = MapProviderError(providerError);
            terminal = ModelRunStateMachine.Fail(
                running,
                leaseToken,
                failure,
                accumulator.Usage,
                streamEvidence,
                completedAt);
        }
        else
        {
            terminal = ModelRunStateMachine.Complete(
                running,
                leaseToken,
                accumulator.Usage,
                streamEvidence,
                accumulator.FinishReason!.Value,
                completedAt);
        }

        if (!terminal.IsSuccess)
        {
            return DomainResult.Fail<ModelRunExecutionResult>(terminal.Failure!);
        }

        var persisted = await PersistTerminalAsync(
            terminal.Value,
            startedLedger,
            terminal.Value.Run.State is ModelRunState.Succeeded
                ? AuditOutcome.Succeeded
                : AuditOutcome.Failed,
            terminal.Value.Run.FailureCode?.ToString(),
            CancellationToken.None);
        return persisted.IsSuccess
            ? DomainResult.Success(new ModelRunExecutionResult(persisted.Value))
            : DomainResult.Fail<ModelRunExecutionResult>(persisted.Failure!);
    }

    private async Task<DomainResult<ModelRunAggregate>> PersistTerminalAsync(
        ModelRunAggregate terminal,
        ModelBudgetLedgerRecord startedLedger,
        AuditOutcome outcome,
        string? errorClassification,
        CancellationToken cancellationToken)
    {
        var currentLedger = await ledgers.FindAsync(terminal.Run.AgentId, cancellationToken) ?? startedLedger;
        var reconciled = ModelBudgetLedgerStateMachine.Reconcile(currentLedger, terminal.Run, clock.UtcNow);
        if (!reconciled.IsSuccess)
        {
            return DomainResult.Fail<ModelRunAggregate>(reconciled.Failure!);
        }

        ModelProviderHealthMutation? healthMutation = null;
        var healthOutcome = terminal.Run.State is ModelRunState.Succeeded
            ? ModelProviderHealthObservationOutcome.Succeeded
            : terminal.Attempt.IsRetryable &&
                terminal.Run.FailureCode is FailureCode.RecoverableExternalFailure
                ? ModelProviderHealthObservationOutcome.RetryableFailure
                : (ModelProviderHealthObservationOutcome?)null;
        if (healthOutcome is { } observedOutcome)
        {
            var currentHealth = await health.FindAsync(terminal.Run.Route.ProfileId, cancellationToken);
            var observedAt = AtLeast(clock.UtcNow, terminal.Run.CompletedAt!.Value);
            var observed = ModelProviderHealthStateMachine.Observe(
                currentHealth,
                new ModelProviderHealthObservation(
                    terminal.Run.InstallationId,
                    terminal.Run.Route.ProfileId,
                    terminal.Run.Id,
                    terminal.Attempt.Id,
                    observedOutcome,
                    terminal.Run.ActorId,
                    terminal.Run.CorrelationId,
                    terminal.Run.CausationId,
                    observedAt));
            if (!observed.IsSuccess)
            {
                return DomainResult.Fail<ModelRunAggregate>(observed.Failure!);
            }

            healthMutation = observed.Value;
        }

        await runs.UpdateAsync(
            terminal,
            terminal.Run.Version - 1,
            terminal.Attempt.Version - 1,
            cancellationToken);
        await ledgers.UpdateAsync(
            reconciled.Value.Ledger,
            reconciled.Value.ExpectedVersion!.Value,
            cancellationToken);
        if (healthMutation is { } mutation)
        {
            if (mutation.IsNew)
            {
                await health.AddAsync(mutation.Record, cancellationToken);
            }
            else
            {
                await health.UpdateAsync(mutation.Record, mutation.ExpectedVersion!.Value, cancellationToken);
            }
        }

        await RecordTerminalAsync(
            terminal,
            reconciled.Value.Ledger,
            healthMutation?.Record,
            outcome,
            errorClassification,
            cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded
            ? DomainResult.Success(terminal)
            : DomainResult.Fail<ModelRunAggregate>(commit.Failure!);
    }

    private async Task RecordStartedAsync(
        ModelRunAggregate aggregate,
        ModelBudgetLedgerRecord ledger,
        CancellationToken cancellationToken)
    {
        await auditRecorder.RecordAsync(new AuditRecordRequest(
            aggregate.Run.InstallationId,
            aggregate.Run.ActorId,
            aggregate.Run.CorrelationId,
            aggregate.Run.CausationId,
            "model.run-started",
            AuditOutcome.Succeeded,
            new
            {
                RunId = aggregate.Run.Id.ToString(),
                AttemptId = aggregate.Attempt.Id.ToString(),
                aggregate.Run.PlanEvidenceHash,
                aggregate.Run.PreparedInputHash,
                aggregate.Run.HealthEvidenceHash,
                ProviderProfileId = aggregate.Run.Route.ProfileId.ToString(),
                aggregate.Run.ProviderVersion,
                LeaseOwner = aggregate.Run.Lease!.Owner,
                aggregate.Run.Lease.TokenHash,
                aggregate.Run.Lease.ExpiresAt,
                aggregate.Run.Reservation.InputTokens,
                aggregate.Run.Reservation.OutputTokens,
                aggregate.Run.Reservation.ToolCalls,
                aggregate.Run.Reservation.Events,
                aggregate.Run.Reservation.WallClockSeconds,
                LedgerVersion = ledger.Version,
            },
            new
            {
                RunState = aggregate.Run.State.ToString(),
                AttemptState = aggregate.Attempt.State.ToString(),
                aggregate.Run.StartedAt,
                aggregate.Run.Version,
            },
            null), cancellationToken);
    }

    private async Task RecordTerminalAsync(
        ModelRunAggregate aggregate,
        ModelBudgetLedgerRecord ledger,
        ModelProviderHealthRecord? healthRecord,
        AuditOutcome outcome,
        string? errorClassification,
        CancellationToken cancellationToken)
    {
        await auditRecorder.RecordAsync(new AuditRecordRequest(
            aggregate.Run.InstallationId,
            aggregate.Run.ActorId,
            aggregate.Run.CorrelationId,
            aggregate.Run.CausationId,
            "model.run-completed",
            outcome,
            new
            {
                RunId = aggregate.Run.Id.ToString(),
                AttemptId = aggregate.Attempt.Id.ToString(),
                aggregate.Run.PlanEvidenceHash,
                aggregate.Run.PreparedInputHash,
                aggregate.Run.StreamEvidence.EventCount,
                aggregate.Run.StreamEvidence.LastSequence,
                aggregate.Run.StreamEvidence.EventStreamHash,
            },
            new
            {
                RunState = aggregate.Run.State.ToString(),
                AttemptState = aggregate.Attempt.State.ToString(),
                aggregate.Run.Usage.InputTokens,
                aggregate.Run.Usage.OutputTokens,
                aggregate.Run.Usage.ToolCalls,
                aggregate.Run.Usage.Cost,
                aggregate.Run.Usage.Currency,
                FinishReason = aggregate.Run.FinishReason?.ToString(),
                FailureCode = aggregate.Run.FailureCode?.ToString(),
                aggregate.Run.CompletedAt,
                LedgerVersion = ledger.Version,
                ledger.ActiveRuns,
                ledger.Consumption.CompletedRuns,
                HealthStatus = healthRecord?.Evidence.Status.ToString(),
                HealthEvidenceCode = healthRecord?.Evidence.EvidenceCode,
                HealthVersion = healthRecord?.Version,
            },
            errorClassification), cancellationToken);
    }

    private static bool RequestMatches(ModelRunExecutionRequest request, ModelRunRecord run) =>
        run.State is ModelRunState.Reserved && run.Version == request.ExpectedRunVersion &&
        run.RequestId == request.Request.Id && run.ActorId == request.ActorId &&
        run.CorrelationId == request.CorrelationId && run.CausationId == request.CausationId;

    private static bool PlanMatchesRun(ModelRoutePlan plan, ModelRunRecord run) =>
        plan.RequestId == run.RequestId && plan.InstallationId == run.InstallationId &&
        plan.InstallationVersion == run.InstallationVersion && plan.AgentId == run.AgentId &&
        plan.AgentVersion == run.AgentVersion && plan.ProviderVersion == run.ProviderVersion &&
        plan.Route.ProfileId == run.Route.ProfileId &&
        FixedEquals(plan.Route.SelectionEvidenceHash, run.Route.SelectionEvidenceHash) &&
        FixedEquals(plan.PreparedInputHash, run.PreparedInputHash) &&
        FixedEquals(plan.HealthEvidenceHash, run.HealthEvidenceHash) &&
        plan.ContextRedactionCount == run.ContextRedactionCount &&
        string.Equals(plan.ContextPreparationPolicy, run.ContextPreparationPolicy, StringComparison.Ordinal) &&
        plan.ReservedInputTokens == run.Reservation.InputTokens &&
        plan.ReservedOutputTokens == run.Reservation.OutputTokens &&
        plan.ReservedToolCalls == run.Reservation.ToolCalls &&
        plan.ReservedEvents == run.Reservation.Events &&
        plan.ReservedWallClockSeconds == run.Reservation.WallClockSeconds &&
        plan.AttemptedProfileIds.SequenceEqual(run.AttemptedProfileIds);

    private static bool DescriptorMatches(IModelProvider? provider, ModelRunRecord run) =>
        provider is not null && provider.Descriptor.ProfileId == run.Route.ProfileId &&
        string.Equals(provider.Descriptor.ProviderType, run.Route.ProviderType, StringComparison.Ordinal) &&
        string.Equals(provider.Descriptor.Model, run.Route.Model, StringComparison.Ordinal);

    private static bool ExceedsReservation(
        ModelRunRecord run,
        ModelUsage usage,
        ModelRunStreamEvidence evidence,
        DateTimeOffset observedAt) =>
        usage.InputTokens > run.Reservation.InputTokens ||
        usage.OutputTokens > run.Reservation.OutputTokens ||
        usage.ToolCalls > run.Reservation.ToolCalls ||
        evidence.EventCount > run.Reservation.Events ||
        run.StartedAt is { } startedAt &&
            observedAt - startedAt >= TimeSpan.FromSeconds(run.Reservation.WallClockSeconds);

    private static DomainFailure MapProviderError(ModelProviderError error) => error.Code switch
    {
        ModelProviderErrorCode.UnsupportedCapability => new DomainFailure(
            FailureCode.UnsupportedCapability,
            "Model provider rejected a required capability."),
        ModelProviderErrorCode.PolicyDenied or ModelProviderErrorCode.AuthenticationFailed => new DomainFailure(
            FailureCode.PolicyDenied,
            "Model provider attempt was denied by current authority."),
        ModelProviderErrorCode.ProviderUnavailable or ModelProviderErrorCode.RateLimited => new DomainFailure(
            FailureCode.RecoverableExternalFailure,
            "Model provider is temporarily unavailable.",
            true),
        _ => new DomainFailure(
            FailureCode.RecoverableExternalFailure,
            "Model provider returned an invalid attempt result.",
            error.IsRetryable),
    };

    private static DomainResult<bool> Validate(ModelRunExecutionRequest request)
    {
        if (request is null || request.RunId.Value == Guid.Empty || request.ExpectedRunVersion < 0 ||
            request.Request is null || !IsBounded(request.ActorId.Value, 256) ||
            !IsBounded(request.WorkerId, 256) || !IsBounded(request.CorrelationId.Value, 128) ||
            request.CausationId is { } causation && !IsBounded(causation.Value, 128) ||
            request.Request.CorrelationId != request.CorrelationId ||
            request.Request.CausationId != request.CausationId)
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Model run execution identity, version, worker, and correlation are invalid."));
        }

        return DomainResult.Success(true);
    }

    private static string CreateLeaseToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var token = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        CryptographicOperations.ZeroMemory(bytes);
        return token;
    }

    private static DateTimeOffset AtLeast(DateTimeOffset value, DateTimeOffset minimum) =>
        value < minimum ? minimum : value;

    private static bool FixedEquals(string first, string second) =>
        first is not null && second is not null && first.Length == second.Length &&
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(first),
            System.Text.Encoding.ASCII.GetBytes(second));

    private static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);

    private static DomainResult<ModelRunExecutionResult> Invalid(string message) =>
        DomainResult.Fail<ModelRunExecutionResult>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<ModelRunExecutionResult> Conflict(string message) =>
        DomainResult.Fail<ModelRunExecutionResult>(new DomainFailure(
            FailureCode.ConcurrencyConflict,
            message,
            true));
}
