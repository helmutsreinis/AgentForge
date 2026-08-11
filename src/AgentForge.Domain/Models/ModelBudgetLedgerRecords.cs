using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Models;

public sealed record ModelBudgetConsumption(
    long InputTokens,
    long OutputTokens,
    long ToolCalls,
    long Events,
    long WallClockSeconds,
    long CompletedRuns);

public sealed record ModelBudgetLedgerRecord(
    InstallationId InstallationId,
    AgentIdentityId AgentId,
    long AgentVersion,
    ModelRunBudgetReservation ActiveReservation,
    int ActiveRuns,
    ModelBudgetConsumption Consumption,
    DateTimeOffset UpdatedAt,
    long Version);

public sealed record ModelBudgetLedgerMutation(
    ModelBudgetLedgerRecord Ledger,
    bool IsNew,
    long? ExpectedVersion);

public static class ModelBudgetLedgerStateMachine
{
    public static DomainResult<ModelBudgetLedgerMutation> Reserve(
        ModelBudgetLedgerRecord? current,
        ModelRunAggregate aggregate,
        AgentBudget budget,
        DateTimeOffset reservedAt) =>
        aggregate is null || aggregate.Attempt.RunId != aggregate.Run.Id ||
            aggregate.Attempt.State is not ModelRunAttemptState.Planned
            ? InvalidMutation("Model budget reservation requires a current attempt.")
            : Reserve(current, aggregate.Run, aggregate.Attempt.Reservation, budget, reservedAt);

    public static DomainResult<ModelBudgetLedgerMutation> Reserve(
        ModelBudgetLedgerRecord? current,
        ModelRunRecord run,
        AgentBudget budget,
        DateTimeOffset reservedAt) =>
        Reserve(current, run, run.Reservation, budget, reservedAt);

    private static DomainResult<ModelBudgetLedgerMutation> Reserve(
        ModelBudgetLedgerRecord? current,
        ModelRunRecord run,
        ModelRunBudgetReservation reservation,
        AgentBudget budget,
        DateTimeOffset reservedAt)
    {
        if (!ValidateRun(run) || !ValidateReservation(reservation) ||
            !ValidateBudget(budget) || reservedAt < run.CreatedAt ||
            current is not null && !ValidateLedger(current))
        {
            return InvalidMutation("Model budget reservation requires valid current run and ledger evidence.");
        }

        var isNew = current is null;
        var ledger = current ?? new ModelBudgetLedgerRecord(
            run.InstallationId,
            run.AgentId,
            run.AgentVersion,
            new ModelRunBudgetReservation(0, 0, 0, 0, 0),
            0,
            new ModelBudgetConsumption(0, 0, 0, 0, 0, 0),
            reservedAt,
            0);
        if (ledger.InstallationId != run.InstallationId || ledger.AgentId != run.AgentId)
        {
            return Conflict("Model budget ledger authority does not match the exact run agent.");
        }

        if (ledger.AgentVersion != run.AgentVersion)
        {
            if (ledger.ActiveRuns != 0 || ledger.ActiveReservation !=
                new ModelRunBudgetReservation(0, 0, 0, 0, 0))
            {
                return Conflict("Active model budget reservations pin the prior agent version.");
            }

            ledger = ledger with { AgentVersion = run.AgentVersion };
        }

        if (!TryAdd(ledger.ActiveReservation.InputTokens, reservation.InputTokens, out var input) ||
            !TryAdd(ledger.ActiveReservation.OutputTokens, reservation.OutputTokens, out var output) ||
            !TryAdd(ledger.ActiveReservation.ToolCalls, reservation.ToolCalls, out var tools) ||
            !TryAdd(ledger.ActiveReservation.Events, reservation.Events, out var events) ||
            !TryAdd(
                ledger.ActiveReservation.WallClockSeconds,
                reservation.WallClockSeconds,
                out var wallClock) ||
            !TryAdd(ledger.ActiveRuns, 1, out var activeRuns))
        {
            return Budget("Model budget reservation totals overflow their durable bounds.");
        }

        if (input > budget.MaxInputTokens || output > budget.MaxOutputTokens ||
            tools > budget.MaxToolInvocations || wallClock > budget.MaxWallClockSeconds)
        {
            return Budget("Concurrent model runs exceed the current agent reservation budget.");
        }

        if (events > 10_000_000 || activeRuns > 1024)
        {
            return Budget("Concurrent model run or event reservations exceed harness bounds.");
        }

        var next = ledger with
        {
            ActiveReservation = new ModelRunBudgetReservation(
                input,
                output,
                tools,
                events,
                wallClock),
            ActiveRuns = activeRuns,
            UpdatedAt = reservedAt,
            Version = isNew ? 0 : checked(ledger.Version + 1),
        };
        return DomainResult.Success(new ModelBudgetLedgerMutation(
            next,
            isNew,
            isNew ? null : ledger.Version));
    }

    public static DomainResult<ModelBudgetLedgerMutation> Reconcile(
        ModelBudgetLedgerRecord current,
        ModelRunAggregate aggregate,
        DateTimeOffset reconciledAt) =>
        aggregate is null || aggregate.Attempt.RunId != aggregate.Run.Id ||
            aggregate.Attempt.State is not (ModelRunAttemptState.Succeeded or
                ModelRunAttemptState.Failed or ModelRunAttemptState.Canceled)
            ? InvalidMutation("Model budget reconciliation requires a current attempt.")
            : Reconcile(current, aggregate.Run, aggregate.Attempt, reconciledAt);

    public static DomainResult<ModelBudgetLedgerMutation> Reconcile(
        ModelBudgetLedgerRecord current,
        ModelRunRecord terminalRun,
        DateTimeOffset reconciledAt) =>
        Reconcile(current, terminalRun, null, reconciledAt);

    private static DomainResult<ModelBudgetLedgerMutation> Reconcile(
        ModelBudgetLedgerRecord current,
        ModelRunRecord terminalRun,
        ModelRunAttemptRecord? terminalAttempt,
        DateTimeOffset reconciledAt)
    {
        var reservation = terminalAttempt?.Reservation ?? terminalRun.Reservation;
        var usage = terminalAttempt?.Usage ?? terminalRun.Usage;
        var streamEvidence = terminalAttempt?.StreamEvidence ?? terminalRun.StreamEvidence;
        var startedAtValue = terminalAttempt?.StartedAt ?? terminalRun.StartedAt;
        var completedAtValue = terminalAttempt?.CompletedAt ?? terminalRun.CompletedAt;
        if (!ValidateLedger(current) || !ValidateRun(terminalRun) ||
            !ValidateReservation(reservation) || usage is null || streamEvidence is null ||
            terminalRun.State is not (ModelRunState.Succeeded or ModelRunState.Failed or
                ModelRunState.Canceled or ModelRunState.BudgetExceeded) ||
            startedAtValue is not { } startedAt || completedAtValue is not { } completedAt ||
            completedAt < startedAt || reconciledAt < completedAt ||
            current.InstallationId != terminalRun.InstallationId ||
            current.AgentId != terminalRun.AgentId || current.AgentVersion != terminalRun.AgentVersion ||
            current.ActiveRuns < 1 || !CanSubtract(current.ActiveReservation, reservation))
        {
            return InvalidMutation("Model budget reconciliation requires one exact terminal active reservation.");
        }

        var elapsedSeconds = (long)Math.Ceiling((completedAt - startedAt).TotalSeconds);
        if (elapsedSeconds is < 0 or > 86_460)
        {
            return InvalidMutation("Model budget reconciliation wall-clock evidence is invalid.");
        }

        if (!TryAdd(current.Consumption.InputTokens, usage.InputTokens, out var input) ||
            !TryAdd(current.Consumption.OutputTokens, usage.OutputTokens, out var output) ||
            !TryAdd(current.Consumption.ToolCalls, usage.ToolCalls, out var tools) ||
            !TryAdd(current.Consumption.Events, streamEvidence.EventCount, out var events) ||
            !TryAdd(current.Consumption.WallClockSeconds, elapsedSeconds, out var wallClock) ||
            !TryAdd(current.Consumption.CompletedRuns, 1, out var completedRuns))
        {
            return InvalidMutation("Model budget consumption totals overflow their durable bounds.");
        }

        var activeReservation = current.ActiveReservation;
        var runReservation = reservation;
        var next = current with
        {
            ActiveReservation = new ModelRunBudgetReservation(
                activeReservation.InputTokens - runReservation.InputTokens,
                activeReservation.OutputTokens - runReservation.OutputTokens,
                activeReservation.ToolCalls - runReservation.ToolCalls,
                activeReservation.Events - runReservation.Events,
                activeReservation.WallClockSeconds - runReservation.WallClockSeconds),
            ActiveRuns = current.ActiveRuns - 1,
            Consumption = new ModelBudgetConsumption(
                input,
                output,
                tools,
                events,
                wallClock,
                completedRuns),
            UpdatedAt = reconciledAt,
            Version = checked(current.Version + 1),
        };
        return DomainResult.Success(new ModelBudgetLedgerMutation(next, false, current.Version));
    }

    private static bool ValidateRun(ModelRunRecord run) =>
        run is not null && run.InstallationId.Value != Guid.Empty && run.AgentId.Value != Guid.Empty &&
        run.AgentVersion >= 1 && run.Reservation is not null && run.Usage is not null &&
        run.StreamEvidence is not null && run.Reservation.InputTokens >= 0 &&
        run.Reservation.OutputTokens >= 1 && run.Reservation.ToolCalls >= 0 &&
        run.Reservation.Events >= 2 && run.Reservation.WallClockSeconds >= 1;

    private static bool ValidateBudget(AgentBudget budget) =>
        budget is not null && budget.MaxInputTokens >= 0 && budget.MaxOutputTokens >= 1 &&
        budget.MaxToolInvocations >= 0 && budget.MaxWallClockSeconds >= 1;

    private static bool ValidateReservation(ModelRunBudgetReservation reservation) =>
        reservation is not null && reservation.InputTokens >= 0 && reservation.OutputTokens >= 1 &&
        reservation.ToolCalls >= 0 && reservation.Events >= 2 && reservation.WallClockSeconds >= 1;

    private static bool ValidateLedger(ModelBudgetLedgerRecord ledger) =>
        ledger is not null && ledger.InstallationId.Value != Guid.Empty && ledger.AgentId.Value != Guid.Empty &&
        ledger.AgentVersion >= 1 && ledger.ActiveReservation is not null && ledger.Consumption is not null &&
        ledger.ActiveRuns >= 0 && ledger.Version >= 0 &&
        ledger.ActiveReservation.InputTokens >= 0 && ledger.ActiveReservation.OutputTokens >= 0 &&
        ledger.ActiveReservation.ToolCalls >= 0 && ledger.ActiveReservation.Events >= 0 &&
        ledger.ActiveReservation.WallClockSeconds >= 0 &&
        ledger.Consumption.InputTokens >= 0 && ledger.Consumption.OutputTokens >= 0 &&
        ledger.Consumption.ToolCalls >= 0 && ledger.Consumption.Events >= 0 &&
        ledger.Consumption.WallClockSeconds >= 0 && ledger.Consumption.CompletedRuns >= 0;

    private static bool CanSubtract(
        ModelRunBudgetReservation total,
        ModelRunBudgetReservation value) =>
        total.InputTokens >= value.InputTokens && total.OutputTokens >= value.OutputTokens &&
        total.ToolCalls >= value.ToolCalls && total.Events >= value.Events &&
        total.WallClockSeconds >= value.WallClockSeconds;

    private static bool TryAdd(long first, long second, out long result)
    {
        result = 0;
        if (first < 0 || second < 0 || first > long.MaxValue - second)
        {
            return false;
        }

        result = first + second;
        return true;
    }

    private static bool TryAdd(int first, int second, out int result)
    {
        result = 0;
        if (first < 0 || second < 0 || first > int.MaxValue - second)
        {
            return false;
        }

        result = first + second;
        return true;
    }

    private static DomainResult<ModelBudgetLedgerMutation> InvalidMutation(string message) =>
        DomainResult.Fail<ModelBudgetLedgerMutation>(new DomainFailure(
            FailureCode.InvalidStateTransition,
            message));

    private static DomainResult<ModelBudgetLedgerMutation> Conflict(string message) =>
        DomainResult.Fail<ModelBudgetLedgerMutation>(new DomainFailure(
            FailureCode.ConcurrencyConflict,
            message,
            true));

    private static DomainResult<ModelBudgetLedgerMutation> Budget(string message) =>
        DomainResult.Fail<ModelBudgetLedgerMutation>(new DomainFailure(FailureCode.BudgetExceeded, message));
}
