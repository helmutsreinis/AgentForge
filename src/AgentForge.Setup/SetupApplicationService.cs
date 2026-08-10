using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Setup;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Setup;

namespace AgentForge.Setup;

internal sealed class SetupApplicationService(
    IInstallationRepository installations,
    IAuditRecorder auditRecorder,
    IUnitOfWork unitOfWork,
    IProviderProfileRepository providerProfiles,
    IProviderProfileValidator providerValidator,
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

    public async Task<DomainResult<ConfigureProviderResult>> ConfigureProviderAsync(
        ConfigureProviderRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestFailure = ValidateActorAndCorrelation(request.ActorId, request.CorrelationId);
        if (requestFailure is not null)
        {
            return DomainResult.Fail<ConfigureProviderResult>(requestFailure);
        }

        var candidateFailure = ValidateCandidate(request.Candidate);
        if (candidateFailure is not null)
        {
            return DomainResult.Fail<ConfigureProviderResult>(candidateFailure);
        }

        var installation = await installations.ReadAsync(cancellationToken);
        if (installation.Id.Value == Guid.Empty || installation.State is not InstallationState.Configuring)
        {
            return DomainResult.Fail<ConfigureProviderResult>(new DomainFailure(
                FailureCode.InvalidStateTransition,
                "A provider can be configured only while installation setup is Configuring."));
        }

        var normalizedName = request.Candidate.Name.Trim();
        var existing = await providerProfiles.FindByNameAsync(
            installation.Id,
            normalizedName,
            cancellationToken);
        if (existing is not null)
        {
            return DomainResult.Fail<ConfigureProviderResult>(new DomainFailure(
                FailureCode.ValidationFailure,
                "A provider profile with this name already exists."));
        }

        var validation = await providerValidator.ValidateAsync(request.Candidate, cancellationToken);
        if (!validation.IsSuccess)
        {
            return DomainResult.Fail<ConfigureProviderResult>(validation.Failure!);
        }

        var now = clock.UtcNow;
        var profile = new ProviderProfile(
            new ProviderProfileId(identifiers.NewGuid()),
            installation.Id,
            normalizedName,
            request.Candidate.ProviderType.Trim().ToLowerInvariant(),
            request.Candidate.Endpoint,
            request.Candidate.Model.Trim(),
            request.Candidate.SecretReference,
            validation.Value,
            0,
            now,
            now,
            request.ActorId,
            request.CorrelationId);
        await providerProfiles.AddAsync(profile, cancellationToken);

        await auditRecorder.RecordAsync(new AuditRecordRequest(
            installation.Id,
            request.ActorId,
            request.CorrelationId,
            installation.CorrelationId,
            "setup.provider-configured",
            AuditOutcome.Succeeded,
            new
            {
                profile.Name,
                profile.ProviderType,
                Endpoint = profile.Endpoint.ToString(),
                profile.Model,
                SecretStore = profile.SecretReference.Store,
                SecretKey = profile.SecretReference.Key,
            },
            new
            {
                profile.Capabilities.TextGeneration,
                profile.Capabilities.Streaming,
                profile.Capabilities.ToolCalls,
                profile.Capabilities.Images,
                profile.Capabilities.EvidenceSource,
            },
            null), cancellationToken);

        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded
            ? DomainResult.Success(new ConfigureProviderResult(profile))
            : DomainResult.Fail<ConfigureProviderResult>(commit.Failure!);
    }

    private static DomainFailure? Validate(BeginSetupRequest request)
    {
        if (request.InstallationId is { Value: var installationId } && installationId == Guid.Empty)
        {
            return new DomainFailure(FailureCode.ValidationFailure, "Installation ID cannot be empty.");
        }

        return ValidateActorAndCorrelation(request.ActorId, request.CorrelationId);
    }

    private static DomainFailure? ValidateActorAndCorrelation(ActorId actorId, CorrelationId correlationId)
    {
        if (string.IsNullOrWhiteSpace(actorId.Value) ||
            actorId.Value.Length > 256 ||
            actorId.Value.Any(char.IsControl))
        {
            return new DomainFailure(FailureCode.ValidationFailure, "Actor ID must contain 1 to 256 characters.");
        }

        if (string.IsNullOrWhiteSpace(correlationId.Value) ||
            correlationId.Value.Length > 128 ||
            correlationId.Value.Any(char.IsControl))
        {
            return new DomainFailure(FailureCode.ValidationFailure, "Correlation ID must contain 1 to 128 characters.");
        }

        return null;
    }

    private static DomainFailure? ValidateCandidate(ProviderProfileCandidate candidate)
    {
        if (candidate is null || candidate.Endpoint is null || candidate.SecretReference is null ||
            string.IsNullOrWhiteSpace(candidate.Name) || candidate.Name.Length > 128 || candidate.Name.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(candidate.ProviderType) || candidate.ProviderType.Length > 64 || candidate.ProviderType.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(candidate.Model) || candidate.Model.Length > 256 || candidate.Model.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(candidate.SecretReference.Store) || candidate.SecretReference.Store.Length > 128 || candidate.SecretReference.Store.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(candidate.SecretReference.Key) || candidate.SecretReference.Key.Length > 512 || candidate.SecretReference.Key.Any(char.IsControl))
        {
            return new DomainFailure(FailureCode.ValidationFailure, "Provider profile fields are missing or invalid.");
        }

        if (!candidate.Endpoint.IsAbsoluteUri ||
            candidate.Endpoint.AbsoluteUri.Length > 2048 ||
            candidate.Endpoint.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(candidate.Endpoint.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Endpoint.Query) ||
            !string.IsNullOrEmpty(candidate.Endpoint.Fragment))
        {
            return new DomainFailure(
                FailureCode.ValidationFailure,
                "Provider endpoint must be an absolute HTTP or HTTPS URI without credentials, query, or fragment.");
        }

        return null;
    }
}
