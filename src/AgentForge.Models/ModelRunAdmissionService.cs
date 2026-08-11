using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;

namespace AgentForge.Models;

internal sealed class ModelRunAdmissionService(
    IModelRunRepository runs,
    IModelRoutePlanner routePlanner,
    IModelContextPreparer contextPreparer,
    ISensitiveDataRedactor redactor,
    IAuditRecorder auditRecorder,
    IUnitOfWork unitOfWork,
    IClock clock,
    IIdentifierGenerator identifiers) : IModelRunAdmissionService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<DomainResult<ModelRunAdmissionResult>> AdmitAsync(
        ModelRunAdmissionRequest request,
        CancellationToken cancellationToken)
    {
        var validation = Validate(request);
        if (!validation.IsSuccess)
        {
            return DomainResult.Fail<ModelRunAdmissionResult>(validation.Failure!);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (redactor.Redact(new
        {
            ActorId = request.ActorId.Value,
            request.IdempotencyKey,
            CorrelationId = request.CorrelationId.Value,
            CausationId = request.CausationId?.Value,
        }).ContainsRedactions)
        {
            return Invalid("Model run admission metadata cannot contain credential-shaped values.");
        }

        var prepared = contextPreparer.Prepare(request.PlanningRequest.Request);
        if (!prepared.IsSuccess)
        {
            return DomainResult.Fail<ModelRunAdmissionResult>(prepared.Failure!);
        }

        var requestHash = ComputeAdmissionRequestHash(request, prepared.Value.InputHash);
        var existing = await runs.FindByIdempotencyKeyAsync(
            request.PlanningRequest.InstallationId,
            request.IdempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            return Replay(existing, requestHash);
        }

        var plan = await routePlanner.PlanAsync(request.PlanningRequest, cancellationToken);
        if (!plan.IsSuccess)
        {
            return DomainResult.Fail<ModelRunAdmissionResult>(plan.Failure!);
        }

        if (!PlanMatches(request.PlanningRequest, plan.Value))
        {
            return DomainResult.Fail<ModelRunAdmissionResult>(new DomainFailure(
                FailureCode.ConcurrencyConflict,
                "Model route plan does not match the exact admission authority request."));
        }

        var reserved = ModelRunStateMachine.Reserve(
            new ModelRunId(identifiers.NewGuid()),
            new ModelRunAttemptId(identifiers.NewGuid()),
            plan.Value,
            request.ActorId,
            requestHash,
            request.IdempotencyKey,
            request.CorrelationId,
            request.CausationId,
            clock.UtcNow);
        if (!reserved.IsSuccess)
        {
            return DomainResult.Fail<ModelRunAdmissionResult>(reserved.Failure!);
        }

        await runs.AddAsync(reserved.Value, cancellationToken);
        await RecordReservationAsync(reserved.Value, cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        if (commit.Succeeded)
        {
            return DomainResult.Success(new ModelRunAdmissionResult(reserved.Value, false));
        }

        if (commit.Failure?.Code is FailureCode.ConcurrencyConflict)
        {
            existing = await runs.FindByIdempotencyKeyAsync(
                request.PlanningRequest.InstallationId,
                request.IdempotencyKey,
                cancellationToken);
            if (existing is not null)
            {
                return Replay(existing, requestHash);
            }
        }

        return DomainResult.Fail<ModelRunAdmissionResult>(commit.Failure!);
    }

    private async Task RecordReservationAsync(
        ModelRunAggregate aggregate,
        CancellationToken cancellationToken)
    {
        var run = aggregate.Run;
        await auditRecorder.RecordAsync(new AuditRecordRequest(
            run.InstallationId,
            run.ActorId,
            run.CorrelationId,
            run.CausationId,
            "model.run-reserved",
            AuditOutcome.Succeeded,
            new
            {
                RunId = run.Id.ToString(),
                AttemptId = aggregate.Attempt.Id.ToString(),
                run.InstallationVersion,
                AgentId = run.AgentId.ToString(),
                run.AgentVersion,
                ProviderProfileId = run.Route.ProfileId.ToString(),
                run.ProviderVersion,
                AttemptedProfileIds = run.AttemptedProfileIds.Select(item => item.ToString()).ToArray(),
                RequestId = run.RequestId.ToString(),
                run.Route.ProviderType,
                run.Route.Model,
                run.Route.IsFallback,
                RequiredCapabilities = run.Route.RequiredCapabilities
                    .OrderBy(item => item)
                    .Select(item => item.ToString())
                    .ToArray(),
                run.Route.SelectionEvidenceHash,
                run.PlanEvidenceHash,
                run.PreparedInputHash,
                run.HealthEvidenceHash,
                run.ContextRedactionCount,
                run.ContextPreparationPolicy,
                run.AdmissionRequestHash,
                run.Reservation.InputTokens,
                run.Reservation.OutputTokens,
                run.Reservation.ToolCalls,
                run.Reservation.Events,
                run.Reservation.WallClockSeconds,
            },
            new
            {
                RunState = run.State.ToString(),
                AttemptState = aggregate.Attempt.State.ToString(),
                run.CreatedAt,
                run.Version,
            },
            null), cancellationToken);
    }

    private static DomainResult<ModelRunAdmissionResult> Replay(
        ModelRunAggregate existing,
        string requestHash) =>
        FixedEquals(existing.Run.AdmissionRequestHash, requestHash)
            ? DomainResult.Success(new ModelRunAdmissionResult(existing, true))
            : DomainResult.Fail<ModelRunAdmissionResult>(new DomainFailure(
                FailureCode.ConcurrencyConflict,
                "Idempotency key is already bound to another model run admission request."));

    private static DomainResult<bool> Validate(ModelRunAdmissionRequest request)
    {
        if (request is null || request.PlanningRequest is null ||
            request.PlanningRequest.Request is null ||
            request.PlanningRequest.InstallationId.Value == Guid.Empty ||
            request.PlanningRequest.AgentId.Value == Guid.Empty ||
            request.PlanningRequest.ExpectedInstallationVersion < 1 ||
            request.PlanningRequest.ExpectedAgentVersion < 1 ||
            request.PlanningRequest.EstimatedInputTokens is < 0 or > 10_000_000 ||
            request.PlanningRequest.AttemptedProfileIds is null ||
            request.PlanningRequest.AttemptedProfileIds.Count > 8 ||
            !IsBounded(request.ActorId.Value, 256) ||
            !IsBounded(request.IdempotencyKey, 256) ||
            !IsBounded(request.CorrelationId.Value, 128) ||
            request.CausationId is { } causation && !IsBounded(causation.Value, 128) ||
            request.PlanningRequest.Request.CorrelationId != request.CorrelationId ||
            request.PlanningRequest.Request.CausationId != request.CausationId)
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Model run admission identity, authority versions, and correlation must be exact and bounded."));
        }

        return DomainResult.Success(true);
    }

    private static bool PlanMatches(ModelRoutePlanningRequest request, ModelRoutePlan plan) =>
        plan is not null && plan.RequestId == request.Request.Id &&
        plan.InstallationId == request.InstallationId &&
        plan.InstallationVersion == request.ExpectedInstallationVersion &&
        plan.AgentId == request.AgentId && plan.AgentVersion == request.ExpectedAgentVersion &&
        plan.ReservedInputTokens == request.EstimatedInputTokens &&
        plan.ReservedOutputTokens == request.Request.Limits.MaximumOutputTokens &&
        plan.ReservedToolCalls == request.Request.Limits.MaximumToolCalls &&
        plan.ReservedEvents == request.Request.Limits.MaximumEvents &&
        plan.ReservedWallClockSeconds == request.Request.Limits.MaximumWallClockSeconds;

    private static string ComputeAdmissionRequestHash(
        ModelRunAdmissionRequest request,
        string contextInputHash)
    {
        var value = new
        {
            InstallationId = request.PlanningRequest.InstallationId.ToString(),
            request.PlanningRequest.ExpectedInstallationVersion,
            AgentId = request.PlanningRequest.AgentId.ToString(),
            request.PlanningRequest.ExpectedAgentVersion,
            RequestId = request.PlanningRequest.Request.Id.ToString(),
            ContextInputHash = contextInputHash,
            request.PlanningRequest.EstimatedInputTokens,
            AttemptedProfileIds = request.PlanningRequest.AttemptedProfileIds
                .OrderBy(item => item.Value)
                .Select(item => item.ToString())
                .ToArray(),
            ActorId = request.ActorId.Value,
            request.IdempotencyKey,
            CorrelationId = request.CorrelationId.Value,
            CausationId = request.CausationId?.Value,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    }

    private static bool FixedEquals(string first, string second) =>
        first is not null && second is not null && first.Length == second.Length &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(first),
            Encoding.ASCII.GetBytes(second));

    private static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);

    private static DomainResult<ModelRunAdmissionResult> Invalid(string message) =>
        DomainResult.Fail<ModelRunAdmissionResult>(new DomainFailure(FailureCode.ValidationFailure, message));
}
