using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Setup;
using AgentForge.Abstractions.Skills;
using AgentForge.Abstractions.Time;
using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Security;
using AgentForge.Domain.Setup;
using AgentForge.Domain.Skills;
using AgentForge.Domain.Tools;

namespace AgentForge.Setup;

internal sealed class SetupProfileEditor(
    IInstallationRepository installations,
    IProviderProfileRepository providers,
    IAgentIdentityRepository agents,
    ISkillRegistryRepository skills,
    IToolCatalog tools,
    IProviderProfileDefinitionEvaluator providerDefinitions,
    IProviderProfileValidator providerValidator,
    IAgentDefinitionEvaluator agentDefinitions,
    ILocalAdministratorAuthenticator authenticator,
    IAuditRecorder auditRecorder,
    IUnitOfWork unitOfWork,
    IClock clock,
    ISecretStore secretStore,
    IIdentifierGenerator identifiers,
    ISensitiveDataRedactor redactor) : ISetupProfileEditor
{
    private const long MaximumReadyOutputTokens = 262_144;
    private static readonly JsonSerializerOptions HashSerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions DisplaySerializerOptions = CreateDisplaySerializerOptions();

    public async Task<DomainResult<ProviderCreatePreview>> PreviewProviderCreateAsync(
        PreviewProviderCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = await PrepareProviderCreationAsync(
            request.ExpectedInstallationVersion,
            request.Candidate,
            request.ProviderCredential,
            request.ActorId,
            request.CorrelationId,
            request.AdministratorCredential,
            cancellationToken);
        if (!prepared.IsSuccess)
        {
            return DomainResult.Fail<ProviderCreatePreview>(prepared.Failure!);
        }

        try
        {
            return DomainResult.Success(new ProviderCreatePreview(
                prepared.Value.PublicCandidate,
                prepared.Value.Profile.Capabilities,
                prepared.Value.UsesCredential,
                prepared.Value.Changes,
                prepared.Value.RequestHash));
        }
        finally
        {
            await DeleteTransientProviderCredentialAsync(prepared.Value, CancellationToken.None);
        }
    }

    public async Task<DomainResult<ProviderCreateResult>> CreateProviderAsync(
        ApplyProviderCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hashFailure = ValidateHash(request.ExpectedRequestHash);
        if (hashFailure is not null)
        {
            return DomainResult.Fail<ProviderCreateResult>(hashFailure);
        }

        var prepared = await PrepareProviderCreationAsync(
            request.ExpectedInstallationVersion,
            request.Candidate,
            request.ProviderCredential,
            request.ActorId,
            request.CorrelationId,
            request.AdministratorCredential,
            cancellationToken);
        if (!prepared.IsSuccess)
        {
            return DomainResult.Fail<ProviderCreateResult>(prepared.Failure!);
        }

        var committed = false;
        try
        {
            if (!string.Equals(prepared.Value.RequestHash, request.ExpectedRequestHash, StringComparison.Ordinal))
            {
                return DomainResult.Fail<ProviderCreateResult>(new DomainFailure(
                    FailureCode.PolicyDenied,
                    "Provider creation parameters or credential do not match the approved preview hash."));
            }

            var installationUpdate = RecordConfigurationChange(
                prepared.Value.Installation,
                request.ActorId,
                request.CorrelationId);
            if (!installationUpdate.IsSuccess)
            {
                return DomainResult.Fail<ProviderCreateResult>(installationUpdate.Failure!);
            }

            await providers.AddAsync(prepared.Value.Profile, cancellationToken);
            await installations.UpdateAsync(
                installationUpdate.Value,
                request.ExpectedInstallationVersion,
                cancellationToken);
            await auditRecorder.RecordAsync(new AuditRecordRequest(
                prepared.Value.Installation.Id,
                request.ActorId,
                request.CorrelationId,
                prepared.Value.Installation.CorrelationId,
                "setup.provider-created",
                AuditOutcome.Succeeded,
                new
                {
                    request.ExpectedInstallationVersion,
                    request.ExpectedRequestHash,
                    prepared.Value.Profile.Name,
                    prepared.Value.Profile.ProviderType,
                    Endpoint = prepared.Value.Profile.Endpoint.AbsoluteUri,
                    prepared.Value.Profile.Model,
                    prepared.Value.UsesCredential,
                },
                new
                {
                    ProviderProfileId = prepared.Value.Profile.Id.ToString(),
                    InstallationVersion = installationUpdate.Value.Version,
                    ProviderVersion = prepared.Value.Profile.Version,
                    prepared.Value.Profile.Capabilities,
                },
                null), cancellationToken);
            var commit = await unitOfWork.CommitAsync(cancellationToken);
            if (!commit.Succeeded)
            {
                return DomainResult.Fail<ProviderCreateResult>(commit.Failure!);
            }

            committed = true;
            return DomainResult.Success(new ProviderCreateResult(
                installationUpdate.Value,
                prepared.Value.Profile,
                prepared.Value.Changes,
                prepared.Value.RequestHash));
        }
        finally
        {
            if (!committed)
            {
                await DeleteTransientProviderCredentialAsync(prepared.Value, CancellationToken.None);
            }
        }
    }

    public async Task<DomainResult<ProviderEditPreview>> PreviewProviderAsync(
        PreviewProviderEditRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = await PrepareProviderAsync(
            request.ProviderProfileId,
            request.ExpectedInstallationVersion,
            request.ExpectedProviderVersion,
            request.Candidate,
            request.ActorId,
            request.CorrelationId,
            request.AdministratorCredential,
            cancellationToken);
        return prepared.IsSuccess
            ? DomainResult.Success(new ProviderEditPreview(
                prepared.Value.Current,
                prepared.Value.Effective,
                prepared.Value.Changes,
                prepared.Value.RequestHash))
            : DomainResult.Fail<ProviderEditPreview>(prepared.Failure!);
    }

    public async Task<DomainResult<ProviderEditResult>> ApplyProviderAsync(
        ApplyProviderEditRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hashFailure = ValidateHash(request.ExpectedRequestHash);
        if (hashFailure is not null)
        {
            return DomainResult.Fail<ProviderEditResult>(hashFailure);
        }

        var prepared = await PrepareProviderAsync(
            request.ProviderProfileId,
            request.ExpectedInstallationVersion,
            request.ExpectedProviderVersion,
            request.Candidate,
            request.ActorId,
            request.CorrelationId,
            request.AdministratorCredential,
            cancellationToken);
        if (!prepared.IsSuccess)
        {
            return DomainResult.Fail<ProviderEditResult>(prepared.Failure!);
        }

        if (!string.Equals(prepared.Value.RequestHash, request.ExpectedRequestHash, StringComparison.Ordinal))
        {
            return DomainResult.Fail<ProviderEditResult>(new DomainFailure(
                FailureCode.PolicyDenied,
                "Provider edit parameters do not match the approved preview hash."));
        }

        if (prepared.Value.Changes.Count == 0)
        {
            return DomainResult.Fail<ProviderEditResult>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Provider edit contains no effective changes."));
        }

        var installationUpdate = RecordConfigurationChange(
            prepared.Value.Installation,
            request.ActorId,
            request.CorrelationId);
        if (!installationUpdate.IsSuccess)
        {
            return DomainResult.Fail<ProviderEditResult>(installationUpdate.Failure!);
        }

        await providers.UpdateAsync(
            prepared.Value.Effective,
            request.ExpectedProviderVersion,
            cancellationToken);
        await installations.UpdateAsync(
            installationUpdate.Value,
            request.ExpectedInstallationVersion,
            cancellationToken);
        await auditRecorder.RecordAsync(new AuditRecordRequest(
            prepared.Value.Installation.Id,
            request.ActorId,
            request.CorrelationId,
            prepared.Value.Installation.CorrelationId,
            "setup.provider-edited",
            AuditOutcome.Succeeded,
            new
            {
                ProviderProfileId = request.ProviderProfileId.ToString(),
                request.ExpectedInstallationVersion,
                request.ExpectedProviderVersion,
                request.ExpectedRequestHash,
                ChangedPaths = prepared.Value.Changes.Select(item => item.Path).ToArray(),
            },
            new
            {
                InstallationVersion = installationUpdate.Value.Version,
                ProviderVersion = prepared.Value.Effective.Version,
            },
            null), cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded
            ? DomainResult.Success(new ProviderEditResult(
                installationUpdate.Value,
                prepared.Value.Effective,
                prepared.Value.Changes,
                prepared.Value.RequestHash))
            : DomainResult.Fail<ProviderEditResult>(commit.Failure!);
    }

    public async Task<DomainResult<AgentEditPreview>> PreviewAgentAsync(
        PreviewAgentEditRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = await PrepareAgentAsync(
            request.AgentIdentityId,
            request.ExpectedInstallationVersion,
            request.ExpectedAgentVersion,
            request.Candidate,
            request.ActorId,
            request.CorrelationId,
            request.AdministratorCredential,
            cancellationToken);
        return prepared.IsSuccess
            ? DomainResult.Success(new AgentEditPreview(
                prepared.Value.Current,
                prepared.Value.EffectiveDefinition,
                prepared.Value.Changes,
                prepared.Value.RequestHash))
            : DomainResult.Fail<AgentEditPreview>(prepared.Failure!);
    }

    public async Task<DomainResult<AgentEditResult>> ApplyAgentAsync(
        ApplyAgentEditRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hashFailure = ValidateHash(request.ExpectedRequestHash);
        if (hashFailure is not null)
        {
            return DomainResult.Fail<AgentEditResult>(hashFailure);
        }

        var prepared = await PrepareAgentAsync(
            request.AgentIdentityId,
            request.ExpectedInstallationVersion,
            request.ExpectedAgentVersion,
            request.Candidate,
            request.ActorId,
            request.CorrelationId,
            request.AdministratorCredential,
            cancellationToken);
        if (!prepared.IsSuccess)
        {
            return DomainResult.Fail<AgentEditResult>(prepared.Failure!);
        }

        if (!string.Equals(prepared.Value.RequestHash, request.ExpectedRequestHash, StringComparison.Ordinal))
        {
            return DomainResult.Fail<AgentEditResult>(new DomainFailure(
                FailureCode.PolicyDenied,
                "Agent edit parameters do not match the approved preview hash."));
        }

        if (prepared.Value.Changes.Count == 0)
        {
            return DomainResult.Fail<AgentEditResult>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Agent edit contains no effective changes."));
        }

        var installationUpdate = RecordConfigurationChange(
            prepared.Value.Installation,
            request.ActorId,
            request.CorrelationId);
        if (!installationUpdate.IsSuccess)
        {
            return DomainResult.Fail<AgentEditResult>(installationUpdate.Failure!);
        }

        await agents.UpdateAsync(
            prepared.Value.EffectiveAgent,
            request.ExpectedAgentVersion,
            cancellationToken);
        await installations.UpdateAsync(
            installationUpdate.Value,
            request.ExpectedInstallationVersion,
            cancellationToken);
        await auditRecorder.RecordAsync(new AuditRecordRequest(
            prepared.Value.Installation.Id,
            request.ActorId,
            request.CorrelationId,
            prepared.Value.Installation.CorrelationId,
            "setup.agent-edited",
            AuditOutcome.Succeeded,
            new
            {
                AgentIdentityId = request.AgentIdentityId.ToString(),
                request.ExpectedInstallationVersion,
                request.ExpectedAgentVersion,
                request.ExpectedRequestHash,
                ChangedPaths = prepared.Value.Changes.Select(item => item.Path).ToArray(),
            },
            new
            {
                InstallationVersion = installationUpdate.Value.Version,
                AgentVersion = prepared.Value.EffectiveAgent.Version,
            },
            null), cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded
            ? DomainResult.Success(new AgentEditResult(
                installationUpdate.Value,
                prepared.Value.EffectiveAgent,
                prepared.Value.EffectiveDefinition,
                prepared.Value.Changes,
                prepared.Value.RequestHash))
            : DomainResult.Fail<AgentEditResult>(commit.Failure!);
    }

    public async Task<DomainResult<AgentCreatePreview>> PreviewAgentCreateAsync(
        PreviewAgentCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = await PrepareAgentCreationAsync(
            request.ExpectedInstallationVersion,
            request.ExpectedProviderVersion,
            request.Candidate,
            request.ActorId,
            request.CorrelationId,
            request.AdministratorCredential,
            cancellationToken);
        return prepared.IsSuccess
            ? DomainResult.Success(new AgentCreatePreview(
                prepared.Value.EffectiveDefinition,
                prepared.Value.Changes,
                prepared.Value.RequestHash))
            : DomainResult.Fail<AgentCreatePreview>(prepared.Failure!);
    }

    public async Task<DomainResult<AgentCreateResult>> CreateAgentAsync(
        ApplyAgentCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hashFailure = ValidateHash(request.ExpectedRequestHash);
        if (hashFailure is not null)
        {
            return DomainResult.Fail<AgentCreateResult>(hashFailure);
        }

        var prepared = await PrepareAgentCreationAsync(
            request.ExpectedInstallationVersion,
            request.ExpectedProviderVersion,
            request.Candidate,
            request.ActorId,
            request.CorrelationId,
            request.AdministratorCredential,
            cancellationToken);
        if (!prepared.IsSuccess)
        {
            return DomainResult.Fail<AgentCreateResult>(prepared.Failure!);
        }
        if (!string.Equals(prepared.Value.RequestHash, request.ExpectedRequestHash, StringComparison.Ordinal))
        {
            return DomainResult.Fail<AgentCreateResult>(new DomainFailure(
                FailureCode.PolicyDenied,
                "Agent creation parameters do not match the approved preview hash."));
        }

        var installationUpdate = RecordConfigurationChange(
            prepared.Value.Installation,
            request.ActorId,
            request.CorrelationId);
        if (!installationUpdate.IsSuccess)
        {
            return DomainResult.Fail<AgentCreateResult>(installationUpdate.Failure!);
        }

        await agents.AddAsync(prepared.Value.Agent, cancellationToken);
        await installations.UpdateAsync(
            installationUpdate.Value,
            request.ExpectedInstallationVersion,
            cancellationToken);
        await auditRecorder.RecordAsync(new AuditRecordRequest(
            prepared.Value.Installation.Id,
            request.ActorId,
            request.CorrelationId,
            prepared.Value.Installation.CorrelationId,
            "setup.agent-created",
            AuditOutcome.Succeeded,
            new
            {
                request.ExpectedInstallationVersion,
                request.ExpectedProviderVersion,
                request.ExpectedRequestHash,
                prepared.Value.Agent.Name,
                ProviderProfileId = prepared.Value.Agent.ModelPolicy.PrimaryProviderProfileId.ToString(),
                ChangedPaths = prepared.Value.Changes.Select(item => item.Path).ToArray(),
            },
            new
            {
                AgentIdentityId = prepared.Value.Agent.Id.ToString(),
                InstallationVersion = installationUpdate.Value.Version,
                AgentVersion = prepared.Value.Agent.Version,
                CapabilityDecisions = prepared.Value.EffectiveDefinition.Capabilities
                    .GroupBy(item => item.Decision)
                    .ToDictionary(group => group.Key.ToString(), group => group.Count()),
            },
            null), cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded
            ? DomainResult.Success(new AgentCreateResult(
                installationUpdate.Value,
                prepared.Value.Agent,
                prepared.Value.EffectiveDefinition,
                prepared.Value.Changes,
                prepared.Value.RequestHash))
            : DomainResult.Fail<AgentCreateResult>(commit.Failure!);
    }

    private async Task<DomainResult<ProviderCreationPreparation>> PrepareProviderCreationAsync(
        long expectedInstallationVersion,
        ProviderProfileCandidate candidate,
        ReadOnlyMemory<char> providerCredential,
        ActorId actorId,
        CorrelationId correlationId,
        ReadOnlyMemory<char> administratorCredential,
        CancellationToken cancellationToken)
    {
        if (candidate is null || !candidate.SecretReference.IsNoCredential)
        {
            return Invalid<ProviderCreationPreparation>(
                "Provider creation accepts credential material separately and never accepts a client secret reference.");
        }

        var normalized = providerDefinitions.NormalizeAndValidate(candidate);
        if (!normalized.IsSuccess)
        {
            return DomainResult.Fail<ProviderCreationPreparation>(normalized.Failure!);
        }
        var credentialFailure = ValidateProviderCredential(providerCredential.Span);
        if (credentialFailure is not null)
        {
            return DomainResult.Fail<ProviderCreationPreparation>(credentialFailure);
        }

        var authorization = await AuthorizeAsync(
            expectedInstallationVersion,
            actorId,
            correlationId,
            administratorCredential,
            cancellationToken);
        if (!authorization.IsSuccess)
        {
            return DomainResult.Fail<ProviderCreationPreparation>(authorization.Failure!);
        }

        var installation = authorization.Value;
        if (await providers.FindByNameAsync(installation.Id, normalized.Value.Name, cancellationToken) is not null)
        {
            return Invalid<ProviderCreationPreparation>("A provider profile with this name already exists.");
        }

        var usesCredential = !providerCredential.IsEmpty;
        var secretReference = SecretReference.NoCredential;
        if (usesCredential)
        {
            var stored = await secretStore.StoreAsync(
                $"provider-{identifiers.NewGuid():N}", providerCredential, cancellationToken);
            if (!stored.IsSuccess)
            {
                return DomainResult.Fail<ProviderCreationPreparation>(stored.Failure!);
            }
            secretReference = stored.Value;
        }

        var effectiveCandidate = normalized.Value with { SecretReference = secretReference };
        var capabilities = await providerValidator.ValidateAsync(effectiveCandidate, cancellationToken);
        if (!capabilities.IsSuccess)
        {
            if (usesCredential)
            {
                _ = await secretStore.DeleteAsync(secretReference, CancellationToken.None);
            }
            return DomainResult.Fail<ProviderCreationPreparation>(capabilities.Failure!);
        }

        var publicCandidate = normalized.Value with { SecretReference = SecretReference.NoCredential };
        var now = clock.UtcNow;
        var profile = new ProviderProfile(
            new ProviderProfileId(identifiers.NewGuid()),
            installation.Id,
            effectiveCandidate.Name,
            effectiveCandidate.ProviderType,
            effectiveCandidate.Endpoint,
            effectiveCandidate.Model,
            effectiveCandidate.SecretReference,
            capabilities.Value,
            0,
            now,
            now,
            actorId,
            correlationId);
        var changes = new List<SetupProfileChange>
        {
            new("provider.name", null, profile.Name),
            new("provider.type", null, profile.ProviderType),
            new("provider.endpoint", null, profile.Endpoint.AbsoluteUri),
            new("provider.model", null, profile.Model),
            new("provider.authentication", null, usesCredential ? "OS-backed secret" : "No credential"),
            new("provider.capabilities", null, Serialize(profile.Capabilities)),
        };
        var credentialHash = ComputeCredentialHash(providerCredential.Span);
        var requestHash = ComputeHash(new
        {
            Kind = "provider-create-v1",
            InstallationId = installation.Id.ToString(),
            expectedInstallationVersion,
            ActorId = actorId.Value,
            CorrelationId = correlationId.Value,
            Candidate = new
            {
                publicCandidate.Name,
                publicCandidate.ProviderType,
                Endpoint = publicCandidate.Endpoint.AbsoluteUri,
                publicCandidate.Model,
            },
            usesCredential,
            credentialHash,
            profile.Capabilities,
        });
        return DomainResult.Success(new ProviderCreationPreparation(
            installation,
            publicCandidate,
            profile,
            usesCredential,
            changes,
            requestHash));
    }

    private async Task<DomainResult<ProviderPreparation>> PrepareProviderAsync(
        ProviderProfileId providerProfileId,
        long expectedInstallationVersion,
        long expectedProviderVersion,
        ProviderProfileCandidate candidate,
        ActorId actorId,
        CorrelationId correlationId,
        ReadOnlyMemory<char> credential,
        CancellationToken cancellationToken)
    {
        if (providerProfileId.Value == Guid.Empty)
        {
            return Invalid<ProviderPreparation>("Provider profile ID cannot be empty.");
        }

        var normalized = providerDefinitions.NormalizeAndValidate(candidate);
        if (!normalized.IsSuccess)
        {
            return DomainResult.Fail<ProviderPreparation>(normalized.Failure!);
        }

        var authorization = await AuthorizeAsync(
            expectedInstallationVersion,
            actorId,
            correlationId,
            credential,
            cancellationToken);
        if (!authorization.IsSuccess)
        {
            return DomainResult.Fail<ProviderPreparation>(authorization.Failure!);
        }

        var installation = authorization.Value;
        var current = await providers.FindByIdAsync(providerProfileId, cancellationToken);
        if (current is null || current.InstallationId != installation.Id)
        {
            return Invalid<ProviderPreparation>("Provider profile does not belong to this installation.");
        }

        if (current.Version != expectedProviderVersion)
        {
            return Conflict<ProviderPreparation>("Provider profile version changed; refresh the edit preview.");
        }

        var duplicate = await providers.FindByNameAsync(installation.Id, normalized.Value.Name, cancellationToken);
        if (duplicate is not null && duplicate.Id != current.Id)
        {
            return Invalid<ProviderPreparation>("A provider profile with this name already exists.");
        }

        var capabilities = await providerValidator.ValidateAsync(normalized.Value, cancellationToken);
        if (!capabilities.IsSuccess)
        {
            return DomainResult.Fail<ProviderPreparation>(capabilities.Failure!);
        }

        var effective = current with
        {
            Name = normalized.Value.Name,
            ProviderType = normalized.Value.ProviderType,
            Endpoint = normalized.Value.Endpoint,
            Model = normalized.Value.Model,
            SecretReference = normalized.Value.SecretReference,
            Capabilities = capabilities.Value,
            Version = checked(current.Version + 1),
            UpdatedAt = NextTimestamp(current.UpdatedAt),
            ActorId = actorId,
            CorrelationId = correlationId,
        };
        var changes = BuildProviderChanges(current, effective);
        if (installation.State is InstallationState.Ready &&
            changes.Any(change => change.Path is not "provider.model"))
        {
            return DomainResult.Fail<ProviderPreparation>(new DomainFailure(
                FailureCode.PolicyDenied,
                "A Ready provider edit may change only the model on the existing connection."));
        }
        var requestHash = ComputeHash(new
        {
            Kind = "provider-edit-v1",
            InstallationId = installation.Id.ToString(),
            ProviderProfileId = providerProfileId.ToString(),
            expectedInstallationVersion,
            expectedProviderVersion,
            ActorId = actorId.Value,
            CorrelationId = correlationId.Value,
            Candidate = normalized.Value,
            effective.Capabilities,
        });
        return DomainResult.Success(new ProviderPreparation(
            installation,
            current,
            effective,
            changes,
            requestHash));
    }

    private async Task<DomainResult<AgentCreationPreparation>> PrepareAgentCreationAsync(
        long expectedInstallationVersion,
        long expectedProviderVersion,
        AgentIdentityCandidate candidate,
        ActorId actorId,
        CorrelationId correlationId,
        ReadOnlyMemory<char> credential,
        CancellationToken cancellationToken)
    {
        var normalized = agentDefinitions.NormalizeAndValidate(candidate);
        if (!normalized.IsSuccess)
        {
            return DomainResult.Fail<AgentCreationPreparation>(normalized.Failure!);
        }

        var authorization = await AuthorizeAsync(
            expectedInstallationVersion,
            actorId,
            correlationId,
            credential,
            cancellationToken);
        if (!authorization.IsSuccess)
        {
            return DomainResult.Fail<AgentCreationPreparation>(authorization.Failure!);
        }

        var installation = authorization.Value;
        if (await agents.FindByNameAsync(installation.Id, normalized.Value.Name, cancellationToken) is not null)
        {
            return Invalid<AgentCreationPreparation>("An agent with this name already exists.");
        }

        var provider = await providers.FindByIdAsync(
            normalized.Value.ModelPolicy.PrimaryProviderProfileId,
            cancellationToken);
        if (provider is null || provider.InstallationId != installation.Id)
        {
            return Invalid<AgentCreationPreparation>("The selected provider profile does not belong to this installation.");
        }
        if (provider.Version != expectedProviderVersion)
        {
            return Conflict<AgentCreationPreparation>("Provider profile version changed; refresh the creation preview.");
        }

        var effectiveDefinition = agentDefinitions.Evaluate(normalized.Value, provider);
        if (!effectiveDefinition.IsSuccess)
        {
            return DomainResult.Fail<AgentCreationPreparation>(effectiveDefinition.Failure!);
        }
        var authority = await ValidateGrantAuthorityAsync(
            installation.Id,
            effectiveDefinition.Value.Agent.CapabilityPolicy.SkillGrants,
            effectiveDefinition.Value.Agent.CapabilityPolicy.ToolGrants,
            cancellationToken);
        if (!authority.IsSuccess)
        {
            return DomainResult.Fail<AgentCreationPreparation>(authority.Failure!);
        }
        var readyBounds = ValidateReadyPolicyBounds(effectiveDefinition.Value.Agent);
        if (readyBounds is not null)
        {
            return DomainResult.Fail<AgentCreationPreparation>(readyBounds);
        }

        var definition = effectiveDefinition.Value.Agent;
        var now = clock.UtcNow;
        var agent = new AgentIdentity(
            new AgentIdentityId(identifiers.NewGuid()),
            installation.Id,
            definition.Name,
            definition.Expertise,
            definition.Mission,
            definition.PreferredLanguage,
            definition.TimeZone,
            definition.ResponseStyle,
            definition.DefaultWorkspace,
            definition.ModelPolicy,
            definition.MemoryPolicy,
            definition.CapabilityPolicy,
            definition.Budget,
            definition.ChildLimits,
            definition.LearningPolicy,
            0,
            now,
            now,
            actorId,
            correlationId);
        var changes = BuildAgentCreationChanges(agent);
        var requestHash = ComputeHash(new
        {
            Kind = "agent-create-v1",
            InstallationId = installation.Id.ToString(),
            expectedInstallationVersion,
            expectedProviderVersion,
            ActorId = actorId.Value,
            CorrelationId = correlationId.Value,
            Candidate = definition,
            provider.Capabilities,
        });
        return DomainResult.Success(new AgentCreationPreparation(
            installation,
            agent,
            effectiveDefinition.Value,
            changes,
            requestHash));
    }

    private async Task<DomainResult<AgentPreparation>> PrepareAgentAsync(
        AgentIdentityId agentIdentityId,
        long expectedInstallationVersion,
        long expectedAgentVersion,
        AgentIdentityCandidate candidate,
        ActorId actorId,
        CorrelationId correlationId,
        ReadOnlyMemory<char> credential,
        CancellationToken cancellationToken)
    {
        if (agentIdentityId.Value == Guid.Empty)
        {
            return Invalid<AgentPreparation>("Agent identity ID cannot be empty.");
        }

        var normalized = agentDefinitions.NormalizeAndValidate(candidate);
        if (!normalized.IsSuccess)
        {
            return DomainResult.Fail<AgentPreparation>(normalized.Failure!);
        }

        var authorization = await AuthorizeAsync(
            expectedInstallationVersion,
            actorId,
            correlationId,
            credential,
            cancellationToken);
        if (!authorization.IsSuccess)
        {
            return DomainResult.Fail<AgentPreparation>(authorization.Failure!);
        }

        var installation = authorization.Value;
        var current = await agents.FindByIdAsync(agentIdentityId, cancellationToken);
        if (current is null || current.InstallationId != installation.Id)
        {
            return Invalid<AgentPreparation>("Agent identity does not belong to this installation.");
        }

        if (current.Version != expectedAgentVersion)
        {
            return Conflict<AgentPreparation>("Agent identity version changed; refresh the edit preview.");
        }

        var duplicate = await agents.FindByNameAsync(installation.Id, normalized.Value.Name, cancellationToken);
        if (duplicate is not null && duplicate.Id != current.Id)
        {
            return Invalid<AgentPreparation>("An agent with this name already exists.");
        }

        var provider = await providers.FindByIdAsync(
            normalized.Value.ModelPolicy.PrimaryProviderProfileId,
            cancellationToken);
        if (provider is null || provider.InstallationId != installation.Id)
        {
            return Invalid<AgentPreparation>("The selected provider profile does not belong to this installation.");
        }

        var effectiveDefinition = agentDefinitions.Evaluate(normalized.Value, provider);
        if (!effectiveDefinition.IsSuccess)
        {
            return DomainResult.Fail<AgentPreparation>(effectiveDefinition.Failure!);
        }

        var definition = effectiveDefinition.Value.Agent;
        var effectiveAgent = current with
        {
            Name = definition.Name,
            Expertise = definition.Expertise,
            Mission = definition.Mission,
            PreferredLanguage = definition.PreferredLanguage,
            TimeZone = definition.TimeZone,
            ResponseStyle = definition.ResponseStyle,
            DefaultWorkspace = definition.DefaultWorkspace,
            ModelPolicy = definition.ModelPolicy,
            MemoryPolicy = definition.MemoryPolicy,
            CapabilityPolicy = definition.CapabilityPolicy,
            Budget = definition.Budget,
            ChildLimits = definition.ChildLimits,
            LearningPolicy = definition.LearningPolicy,
            Version = checked(current.Version + 1),
            UpdatedAt = NextTimestamp(current.UpdatedAt),
            ActorId = actorId,
            CorrelationId = correlationId,
        };
        var changes = BuildAgentChanges(current, effectiveAgent);
        if (installation.State is InstallationState.Ready)
        {
            var readyBounds = ValidateReadyPolicyBounds(definition);
            if (readyBounds is not null)
            {
                return DomainResult.Fail<AgentPreparation>(readyBounds);
            }

            var addedSkills = effectiveAgent.CapabilityPolicy.SkillGrants
                .Except(current.CapabilityPolicy.SkillGrants, StringComparer.Ordinal)
                .ToArray();
            var addedTools = effectiveAgent.CapabilityPolicy.ToolGrants
                .Except(current.CapabilityPolicy.ToolGrants, StringComparer.Ordinal)
                .ToArray();
            var authority = await ValidateGrantAuthorityAsync(
                installation.Id, addedSkills, addedTools, cancellationToken);
            if (!authority.IsSuccess)
            {
                return DomainResult.Fail<AgentPreparation>(authority.Failure!);
            }
        }
        var requestHash = ComputeHash(new
        {
            Kind = "agent-edit-v1",
            InstallationId = installation.Id.ToString(),
            AgentIdentityId = agentIdentityId.ToString(),
            expectedInstallationVersion,
            expectedAgentVersion,
            ActorId = actorId.Value,
            CorrelationId = correlationId.Value,
            Candidate = definition,
            ProviderVersion = provider.Version,
            provider.Capabilities,
        });
        return DomainResult.Success(new AgentPreparation(
            installation,
            current,
            effectiveAgent,
            effectiveDefinition.Value,
            changes,
            requestHash));
    }

    private async Task<DomainResult<InstallationSnapshot>> AuthorizeAsync(
        long expectedInstallationVersion,
        ActorId actorId,
        CorrelationId correlationId,
        ReadOnlyMemory<char> credential,
        CancellationToken cancellationToken)
    {
        var identityFailure = ValidateIdentity(actorId, correlationId);
        if (identityFailure is not null)
        {
            return DomainResult.Fail<InstallationSnapshot>(identityFailure);
        }

        var installation = await installations.ReadAsync(cancellationToken);
        if (installation.Id.Value == Guid.Empty)
        {
            return DomainResult.Fail<InstallationSnapshot>(new DomainFailure(
                FailureCode.InvalidStateTransition,
                "Installation is uninitialized."));
        }

        var authentication = await authenticator.AuthenticateAsync(
            installation.Id,
            credential,
            cancellationToken);
        if (!authentication.IsSuccess || authentication.Value != actorId)
        {
            return DomainResult.Fail<InstallationSnapshot>(new DomainFailure(
                FailureCode.PolicyDenied,
                "The authenticated administrator does not match the requested actor."));
        }

        if (installation.Version != expectedInstallationVersion)
        {
            return Conflict<InstallationSnapshot>("Installation version changed; refresh the edit preview.");
        }

        if (installation.State is not (InstallationState.Configuring or InstallationState.Ready))
        {
            return DomainResult.Fail<InstallationSnapshot>(new DomainFailure(
                FailureCode.InvalidStateTransition,
                "Profiles can be edited only while installation state is Configuring or Ready."));
        }

        return DomainResult.Success(installation);
    }

    private DomainFailure? ValidateIdentity(ActorId actorId, CorrelationId correlationId)
    {
        if (string.IsNullOrWhiteSpace(actorId.Value) || actorId.Value.Length > 256 || actorId.Value.Any(char.IsControl))
        {
            return new DomainFailure(FailureCode.ValidationFailure, "Actor ID must contain 1 to 256 printable characters.");
        }

        if (string.IsNullOrWhiteSpace(correlationId.Value) || correlationId.Value.Length > 128 || correlationId.Value.Any(char.IsControl))
        {
            return new DomainFailure(FailureCode.ValidationFailure, "Correlation ID must contain 1 to 128 printable characters.");
        }

        return redactor.Redact(new[] { actorId.Value, correlationId.Value }).ContainsRedactions
            ? new DomainFailure(
                FailureCode.ValidationFailure,
                "Actor and correlation IDs cannot contain credential-shaped content.")
            : null;
    }

    private DomainResult<InstallationSnapshot> RecordConfigurationChange(
        InstallationSnapshot installation,
        ActorId actorId,
        CorrelationId correlationId) => InstallationStateMachine.Transition(
            installation,
            InstallationTrigger.ConfigurationChanged,
            installation.UpdatedAt < clock.UtcNow ? clock.UtcNow : installation.UpdatedAt.AddTicks(1),
            actorId,
            correlationId);

    private DateTimeOffset NextTimestamp(DateTimeOffset current) =>
        current < clock.UtcNow ? clock.UtcNow : current.AddTicks(1);

    private static List<SetupProfileChange> BuildProviderChanges(
        ProviderProfile current,
        ProviderProfile effective)
    {
        var changes = new List<SetupProfileChange>();
        AddChange(changes, "provider.name", current.Name, effective.Name);
        AddChange(changes, "provider.type", current.ProviderType, effective.ProviderType);
        AddChange(changes, "provider.endpoint", current.Endpoint.AbsoluteUri, effective.Endpoint.AbsoluteUri);
        AddChange(changes, "provider.model", current.Model, effective.Model);
        AddChange(changes, "provider.secretReference", Reference(current.SecretReference), Reference(effective.SecretReference));
        AddChange(changes, "provider.capabilities", Serialize(current.Capabilities), Serialize(effective.Capabilities));
        return changes;
    }

    private static List<SetupProfileChange> BuildAgentChanges(
        AgentIdentity current,
        AgentIdentity effective)
    {
        var changes = new List<SetupProfileChange>();
        AddChange(changes, "agent.name", current.Name, effective.Name);
        AddChange(changes, "agent.expertise", current.Expertise, effective.Expertise);
        AddChange(changes, "agent.mission", current.Mission, effective.Mission);
        AddChange(changes, "agent.preferredLanguage", current.PreferredLanguage, effective.PreferredLanguage);
        AddChange(changes, "agent.timeZone", current.TimeZone, effective.TimeZone);
        AddChange(changes, "agent.responseStyle", current.ResponseStyle, effective.ResponseStyle);
        AddChange(changes, "agent.defaultWorkspace", current.DefaultWorkspace, effective.DefaultWorkspace);
        AddChange(changes, "agent.modelPolicy", Serialize(current.ModelPolicy), Serialize(effective.ModelPolicy));
        AddChange(changes, "agent.memoryPolicy", Serialize(current.MemoryPolicy), Serialize(effective.MemoryPolicy));
        AddChange(changes, "agent.capabilityPolicy", Serialize(current.CapabilityPolicy), Serialize(effective.CapabilityPolicy));
        AddChange(changes, "agent.budget", Serialize(current.Budget), Serialize(effective.Budget));
        AddChange(changes, "agent.childLimits", Serialize(current.ChildLimits), Serialize(effective.ChildLimits));
        AddChange(changes, "agent.learningPolicy", Serialize(current.LearningPolicy), Serialize(effective.LearningPolicy));
        return changes;
    }

    private static List<SetupProfileChange> BuildAgentCreationChanges(AgentIdentity agent) =>
    [
        new("agent.name", null, agent.Name),
        new("agent.expertise", null, agent.Expertise),
        new("agent.mission", null, agent.Mission),
        new("agent.preferredLanguage", null, agent.PreferredLanguage),
        new("agent.timeZone", null, agent.TimeZone),
        new("agent.responseStyle", null, agent.ResponseStyle),
        new("agent.defaultWorkspace", null, agent.DefaultWorkspace),
        new("agent.modelPolicy", null, Serialize(agent.ModelPolicy)),
        new("agent.memoryPolicy", null, Serialize(agent.MemoryPolicy)),
        new("agent.capabilityPolicy", null, Serialize(agent.CapabilityPolicy)),
        new("agent.budget", null, Serialize(agent.Budget)),
        new("agent.childLimits", null, Serialize(agent.ChildLimits)),
        new("agent.learningPolicy", null, Serialize(agent.LearningPolicy)),
    ];

    private static DomainFailure? ValidateReadyPolicyBounds(AgentIdentityCandidate candidate)
    {
        if (candidate.Budget.MaxOutputTokens is < 256 or > MaximumReadyOutputTokens)
        {
            return new DomainFailure(
                FailureCode.PolicyDenied,
                $"Ready agent output budgets must remain between 256 and {MaximumReadyOutputTokens:N0} tokens.");
        }
        if (candidate.Budget.MaxToolInvocations is < 0 or > 1000 ||
            candidate.CapabilityPolicy.ToolGrants.Count == 0 && candidate.Budget.MaxToolInvocations != 0 ||
            candidate.CapabilityPolicy.ToolGrants.Count > 0 && candidate.Budget.MaxToolInvocations == 0)
        {
            return new DomainFailure(
                FailureCode.PolicyDenied,
                "Ready tool invocation budgets must be zero without grants and between 1 and 1,000 when tools are granted.");
        }

        return null;
    }

    private async Task<DomainResult<bool>> ValidateGrantAuthorityAsync(
        InstallationId installationId,
        IReadOnlyList<string> skillGrants,
        IReadOnlyList<string> toolGrants,
        CancellationToken cancellationToken)
    {
        foreach (var skillGrant in skillGrants)
        {
            if (await skills.FindActiveAsync(
                    installationId, new SkillId(skillGrant), cancellationToken) is null)
            {
                return DomainResult.Fail<bool>(new DomainFailure(
                    FailureCode.PolicyDenied,
                    $"Skill grant '{skillGrant}' requires an exact promoted Active version."));
            }
        }

        foreach (var toolGrant in toolGrants)
        {
            var available = await tools.SearchAsync(
                new ToolSearchRequest(string.Empty, toolGrant, null, 50), cancellationToken);
            if (!available.IsSuccess || !available.Value.Any(item =>
                    string.Equals(item.CapabilityId, toolGrant, StringComparison.Ordinal)))
            {
                return DomainResult.Fail<bool>(new DomainFailure(
                    FailureCode.PolicyDenied,
                    $"Tool grant '{toolGrant}' requires an exact descriptor in the authoritative catalog."));
            }
        }

        return DomainResult.Success(true);
    }

    private static DomainFailure? ValidateProviderCredential(ReadOnlySpan<char> credential)
    {
        if (credential.Length > 8192 || credential.ContainsAnyInRange('\0', '\u001f') || credential.Contains('\u007f'))
        {
            return new DomainFailure(
                FailureCode.ValidationFailure,
                "Provider credentials must contain at most 8,192 printable characters.");
        }

        return null;
    }

    private static string ComputeCredentialHash(ReadOnlySpan<char> credential)
    {
        if (credential.IsEmpty)
        {
            return "none";
        }

        var characters = credential.ToArray();
        var bytes = Encoding.UTF8.GetBytes(characters);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            Array.Clear(characters);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task DeleteTransientProviderCredentialAsync(
        ProviderCreationPreparation preparation,
        CancellationToken cancellationToken)
    {
        if (preparation.UsesCredential)
        {
            _ = await secretStore.DeleteAsync(preparation.Profile.SecretReference, cancellationToken);
        }
    }

    private static void AddChange(
        List<SetupProfileChange> changes,
        string path,
        string? before,
        string? after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            changes.Add(new SetupProfileChange(path, before, after));
        }
    }

    private static string ComputeHash(object value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, HashSerializerOptions)))
            .ToLowerInvariant();

    private static JsonSerializerOptions CreateDisplaySerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, DisplaySerializerOptions);

    private static string Reference(SecretReference value) => $"{value.Store}:{value.Key}";

    private static DomainFailure? ValidateHash(string value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit) && value.All(character => !char.IsAsciiLetter(character) || char.IsLower(character))
            ? null
            : new DomainFailure(FailureCode.ValidationFailure, "Expected preview hash must be 64 lowercase hexadecimal characters.");

    private static DomainResult<T> Invalid<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<T> Conflict<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ConcurrencyConflict, message, IsRetryable: true));

    private sealed record ProviderPreparation(
        InstallationSnapshot Installation,
        ProviderProfile Current,
        ProviderProfile Effective,
        IReadOnlyList<SetupProfileChange> Changes,
        string RequestHash);

    private sealed record ProviderCreationPreparation(
        InstallationSnapshot Installation,
        ProviderProfileCandidate PublicCandidate,
        ProviderProfile Profile,
        bool UsesCredential,
        IReadOnlyList<SetupProfileChange> Changes,
        string RequestHash);

    private sealed record AgentPreparation(
        InstallationSnapshot Installation,
        AgentIdentity Current,
        AgentIdentity EffectiveAgent,
        EffectiveAgentDefinition EffectiveDefinition,
        IReadOnlyList<SetupProfileChange> Changes,
        string RequestHash);

    private sealed record AgentCreationPreparation(
        InstallationSnapshot Installation,
        AgentIdentity Agent,
        EffectiveAgentDefinition EffectiveDefinition,
        IReadOnlyList<SetupProfileChange> Changes,
        string RequestHash);
}
