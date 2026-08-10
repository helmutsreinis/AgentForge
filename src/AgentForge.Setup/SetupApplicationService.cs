using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Setup;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Security;
using AgentForge.Domain.Setup;

namespace AgentForge.Setup;

internal sealed class SetupApplicationService(
    IInstallationRepository installations,
    IAuditRecorder auditRecorder,
    IUnitOfWork unitOfWork,
    IProviderProfileRepository providerProfiles,
    IProviderProfileValidator providerValidator,
    IAgentIdentityRepository agents,
    IAgentDefinitionEvaluator agentDefinitionEvaluator,
    ILocalAdministratorRepository administrators,
    ILocalAdministratorCredentialService administratorCredentials,
    ISecretStore secretStore,
    IAuditIntegrityVerifier auditIntegrityVerifier,
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

    public async Task<DomainResult<EffectiveAgentDefinition>> PreviewAgentAsync(
        PreviewAgentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = await PrepareAgentAsync(
            request.Candidate,
            request.ActorId,
            request.CorrelationId,
            cancellationToken);
        return prepared.IsSuccess
            ? DomainResult.Success(prepared.Value.EffectiveDefinition)
            : DomainResult.Fail<EffectiveAgentDefinition>(prepared.Failure!);
    }

    public async Task<DomainResult<CreateAgentResult>> CreateAgentAsync(
        CreateAgentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = await PrepareAgentAsync(
            request.Candidate,
            request.ActorId,
            request.CorrelationId,
            cancellationToken);
        if (!prepared.IsSuccess)
        {
            return DomainResult.Fail<CreateAgentResult>(prepared.Failure!);
        }

        var preparation = prepared.Value;
        var now = clock.UtcNow;
        var candidate = preparation.EffectiveDefinition.Agent;
        var agent = new AgentIdentity(
            new AgentIdentityId(identifiers.NewGuid()),
            preparation.Installation.Id,
            candidate.Name,
            candidate.Expertise,
            candidate.Mission,
            candidate.PreferredLanguage,
            candidate.TimeZone,
            candidate.ResponseStyle,
            candidate.DefaultWorkspace,
            candidate.ModelPolicy,
            candidate.MemoryPolicy,
            candidate.CapabilityPolicy,
            candidate.Budget,
            candidate.ChildLimits,
            candidate.LearningPolicy,
            0,
            now,
            now,
            request.ActorId,
            request.CorrelationId);
        await agents.AddAsync(agent, cancellationToken);

        await auditRecorder.RecordAsync(new AuditRecordRequest(
            preparation.Installation.Id,
            request.ActorId,
            request.CorrelationId,
            preparation.Installation.CorrelationId,
            "setup.agent-created",
            AuditOutcome.Succeeded,
            new
            {
                agent.Name,
                agent.PreferredLanguage,
                agent.TimeZone,
                agent.ModelPolicy.PrimaryProviderProfileId,
                agent.ModelPolicy.DataLocality,
                agent.MemoryPolicy.Scope,
                agent.CapabilityPolicy.NetworkPosture,
                ToolGrantCount = agent.CapabilityPolicy.ToolGrants.Count,
                SkillGrantCount = agent.CapabilityPolicy.SkillGrants.Count,
                agent.LearningPolicy.Mode,
                agent.LearningPolicy.MutableSkillScope,
            },
            new
            {
                agent.Id,
                agent.Budget.MaxTurns,
                agent.Budget.MaxToolInvocations,
                agent.Budget.MaxInputTokens,
                agent.Budget.MaxOutputTokens,
                agent.Budget.MaxWallClockSeconds,
                agent.ChildLimits.MaxDepth,
                agent.ChildLimits.MaxChildren,
                agent.ChildLimits.MaxConcurrency,
                agent.ChildLimits.MaxTotalTokens,
                CapabilityDecisions = preparation.EffectiveDefinition.Capabilities
                    .GroupBy(item => item.Decision)
                    .ToDictionary(group => group.Key.ToString(), group => group.Count()),
            },
            null), cancellationToken);

        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded
            ? DomainResult.Success(new CreateAgentResult(agent, preparation.EffectiveDefinition))
            : DomainResult.Fail<CreateAgentResult>(commit.Failure!);
    }

    public async Task<DomainResult<SetupCompletionReport>> CompleteAsync(
        CompleteSetupRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestFailure = ValidateActorAndCorrelation(request.ActorId, request.CorrelationId);
        if (requestFailure is not null)
        {
            return DomainResult.Fail<SetupCompletionReport>(requestFailure);
        }

        var installation = await installations.ReadAsync(cancellationToken);
        if (installation.Id.Value == Guid.Empty || installation.State is not InstallationState.Configuring)
        {
            return DomainResult.Fail<SetupCompletionReport>(new DomainFailure(
                FailureCode.InvalidStateTransition,
                "Setup can be completed only while installation state is Configuring."));
        }

        if (await administrators.FindAsync(installation.Id, cancellationToken) is not null)
        {
            return DomainResult.Fail<SetupCompletionReport>(new DomainFailure(
                FailureCode.InvalidStateTransition,
                "This installation already has a local administrator."));
        }

        var providers = await providerProfiles.ListAsync(installation.Id, cancellationToken);
        var usableProviders = providers.Where(item => item.Capabilities.TextGeneration).ToArray();
        if (usableProviders.Length == 0)
        {
            return DomainResult.Fail<SetupCompletionReport>(new DomainFailure(
                FailureCode.UnsupportedCapability,
                "Setup requires at least one validated text-generation provider."));
        }

        var configuredAgents = await agents.ListAsync(installation.Id, cancellationToken);
        if (configuredAgents.Count == 0)
        {
            return DomainResult.Fail<SetupCompletionReport>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Setup requires at least one named agent identity."));
        }

        var auditVerification = await auditIntegrityVerifier.VerifyAsync(cancellationToken);
        if (!auditVerification.IsValid)
        {
            return DomainResult.Fail<SetupCompletionReport>(new DomainFailure(
                FailureCode.PolicyDenied,
                "The audit chain is invalid; setup must enter recovery instead of Ready."));
        }

        var secretCapability = secretStore.GetCapability();
        if (!secretCapability.IsAvailable)
        {
            return DomainResult.Fail<SetupCompletionReport>(secretCapability.UnavailableReason!);
        }

        foreach (var provider in usableProviders)
        {
            var materialized = await secretStore.MaterializeAsync(provider.SecretReference, cancellationToken);
            if (!materialized.IsSuccess)
            {
                return DomainResult.Fail<SetupCompletionReport>(new DomainFailure(
                    FailureCode.RecoverableExternalFailure,
                    $"Provider profile '{provider.Name}' has a non-materializable secret reference.",
                    IsRetryable: true));
            }

            await materialized.Value.DisposeAsync();
        }

        var generated = await administratorCredentials.CreateAsync(
            $"administrator-{installation.Id}",
            cancellationToken);
        if (!generated.IsSuccess)
        {
            return DomainResult.Fail<SetupCompletionReport>(generated.Failure!);
        }

        var credentialReference = generated.Value.ClientCredentialReference;
        var committed = false;
        try
        {
            var validating = InstallationStateMachine.Transition(
                installation,
                InstallationTrigger.BeginValidation,
                clock.UtcNow,
                request.ActorId,
                request.CorrelationId);
            if (!validating.IsSuccess)
            {
                return DomainResult.Fail<SetupCompletionReport>(validating.Failure!);
            }

            var ready = InstallationStateMachine.Transition(
                validating.Value,
                InstallationTrigger.ValidationSucceeded,
                clock.UtcNow,
                request.ActorId,
                request.CorrelationId);
            if (!ready.IsSuccess)
            {
                return DomainResult.Fail<SetupCompletionReport>(ready.Failure!);
            }

            var now = clock.UtcNow;
            var administrator = new LocalAdministrator(
                new AdministratorIdentityId(identifiers.NewGuid()),
                installation.Id,
                request.ActorId,
                credentialReference,
                generated.Value.CredentialVerifier,
                0,
                now,
                now,
                request.CorrelationId);
            await administrators.AddAsync(administrator, cancellationToken);
            await installations.UpdateAsync(ready.Value, installation.Version, cancellationToken);

            var checks = new[]
            {
                new SetupValidationCheck("storage.migrations", true, "Durable schema is current."),
                new SetupValidationCheck("audit.integrity", true, $"Verified {auditVerification.VerifiedEventCount} audit events."),
                new SetupValidationCheck("provider.text", true, $"Validated {usableProviders.Length} text provider profile(s)."),
                new SetupValidationCheck("provider.secret", true, "All usable provider secret references materialize."),
                new SetupValidationCheck("agent.identity", true, $"Validated {configuredAgents.Count} named agent identity profile(s)."),
                new SetupValidationCheck("administrator.local", true, "Created an OS-protected local administrator credential."),
            };
            await auditRecorder.RecordAsync(new AuditRecordRequest(
                installation.Id,
                request.ActorId,
                request.CorrelationId,
                installation.CorrelationId,
                "setup.completed",
                AuditOutcome.Succeeded,
                new
                {
                    PreviousState = installation.State.ToString(),
                    ProviderCount = usableProviders.Length,
                    AgentCount = configuredAgents.Count,
                    SecretStore = credentialReference.Store,
                },
                new
                {
                    State = ready.Value.State.ToString(),
                    ready.Value.Version,
                    AdministratorId = administrator.Id,
                    CheckIds = checks.Select(item => item.CheckId).ToArray(),
                },
                null), cancellationToken);

            var commit = await unitOfWork.CommitAsync(cancellationToken);
            if (!commit.Succeeded)
            {
                return DomainResult.Fail<SetupCompletionReport>(commit.Failure!);
            }

            committed = true;
            return DomainResult.Success(new SetupCompletionReport(ready.Value, administrator, checks));
        }
        finally
        {
            if (!committed)
            {
                _ = await secretStore.DeleteAsync(credentialReference, CancellationToken.None);
            }
        }
    }

    private async Task<DomainResult<AgentPreparation>> PrepareAgentAsync(
        AgentIdentityCandidate candidate,
        ActorId actorId,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        var requestFailure = ValidateActorAndCorrelation(actorId, correlationId);
        if (requestFailure is not null)
        {
            return DomainResult.Fail<AgentPreparation>(requestFailure);
        }

        var normalized = agentDefinitionEvaluator.NormalizeAndValidate(candidate);
        if (!normalized.IsSuccess)
        {
            return DomainResult.Fail<AgentPreparation>(normalized.Failure!);
        }

        var installation = await installations.ReadAsync(cancellationToken);
        if (installation.Id.Value == Guid.Empty || installation.State is not InstallationState.Configuring)
        {
            return DomainResult.Fail<AgentPreparation>(new DomainFailure(
                FailureCode.InvalidStateTransition,
                "An agent can be configured only while installation setup is Configuring."));
        }

        var existing = await agents.FindByNameAsync(installation.Id, normalized.Value.Name, cancellationToken);
        if (existing is not null)
        {
            return DomainResult.Fail<AgentPreparation>(new DomainFailure(
                FailureCode.ValidationFailure,
                "An agent identity with this name already exists."));
        }

        var provider = await providerProfiles.FindByIdAsync(
            normalized.Value.ModelPolicy.PrimaryProviderProfileId,
            cancellationToken);
        if (provider is null || provider.InstallationId != installation.Id)
        {
            return DomainResult.Fail<AgentPreparation>(new DomainFailure(
                FailureCode.ValidationFailure,
                "The selected provider profile does not exist in this installation."));
        }

        var effectiveDefinition = agentDefinitionEvaluator.Evaluate(normalized.Value, provider);
        return effectiveDefinition.IsSuccess
            ? DomainResult.Success(new AgentPreparation(installation, effectiveDefinition.Value))
            : DomainResult.Fail<AgentPreparation>(effectiveDefinition.Failure!);
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

    private sealed record AgentPreparation(
        InstallationSnapshot Installation,
        EffectiveAgentDefinition EffectiveDefinition);
}
