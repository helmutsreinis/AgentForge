using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Setup;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Setup;

namespace AgentForge.Setup;

internal sealed class SetupApplicationService(
    IInstallationRepository installations,
    IAuditRecorder auditRecorder,
    IUnitOfWork unitOfWork,
    IClock clock,
    IIdentifierGenerator identifiers) : ISetupApplicationService
{
    public async Task<DomainResult<BeginSetupResult>> BeginAsync(
        BeginSetupRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validationFailure = Validate(request);
        if (validationFailure is not null)
        {
            return DomainResult.Fail<BeginSetupResult>(validationFailure);
        }

        var current = await installations.ReadAsync(cancellationToken);
        var isNew = current.Id.Value == Guid.Empty;
        if (isNew)
        {
            current = InstallationSnapshot.CreateUninitialized(
                request.InstallationId ?? new InstallationId(identifiers.NewGuid()),
                clock.UtcNow,
                request.ActorId,
                request.CorrelationId);
        }
        else if (request.InstallationId is not null && request.InstallationId != current.Id)
        {
            return DomainResult.Fail<BeginSetupResult>(new DomainFailure(
                FailureCode.ValidationFailure,
                "The requested installation ID does not match the durable installation."));
        }

        var transition = InstallationStateMachine.Transition(
            current,
            InstallationTrigger.BeginConfiguration,
            clock.UtcNow,
            request.ActorId,
            request.CorrelationId);
        if (!transition.IsSuccess)
        {
            return DomainResult.Fail<BeginSetupResult>(transition.Failure!);
        }

        var next = transition.Value;
        if (isNew)
        {
            await installations.AddAsync(next, cancellationToken);
        }
        else
        {
            await installations.UpdateAsync(next, current.Version, cancellationToken);
        }

        var audit = await auditRecorder.RecordAsync(new AuditRecordRequest(
            next.Id,
            request.ActorId,
            request.CorrelationId,
            null,
            "setup.configuration-begun",
            AuditOutcome.Succeeded,
            new
            {
                InstallationId = next.Id.ToString(),
                PreviousState = current.State.ToString(),
                ExpectedVersion = current.Version,
            },
            new
            {
                State = next.State.ToString(),
                next.Version,
            },
            null), cancellationToken);

        var commit = await unitOfWork.CommitAsync(cancellationToken);
        if (!commit.Succeeded)
        {
            return DomainResult.Fail<BeginSetupResult>(commit.Failure!);
        }

        return DomainResult.Success(new BeginSetupResult(next, audit.Event));
    }

    private static DomainFailure? Validate(BeginSetupRequest request)
    {
        if (request.InstallationId is { Value: var installationId } && installationId == Guid.Empty)
        {
            return new DomainFailure(FailureCode.ValidationFailure, "Installation ID cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(request.ActorId.Value) ||
            request.ActorId.Value.Length > 256 ||
            request.ActorId.Value.Any(char.IsControl))
        {
            return new DomainFailure(FailureCode.ValidationFailure, "Actor ID must contain 1 to 256 characters.");
        }

        if (string.IsNullOrWhiteSpace(request.CorrelationId.Value) ||
            request.CorrelationId.Value.Length > 128 ||
            request.CorrelationId.Value.Any(char.IsControl))
        {
            return new DomainFailure(FailureCode.ValidationFailure, "Correlation ID must contain 1 to 128 characters.");
        }

        return null;
    }
}
