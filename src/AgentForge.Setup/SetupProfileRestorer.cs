using System.Security.Cryptography;
using System.Text.Json;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Artifacts;
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

internal sealed class SetupProfileRestorer(
    IInstallationRepository installations,
    IProviderProfileRepository providers,
    IAgentIdentityRepository agents,
    ILocalAdministratorRepository administrators,
    ISetupProfileSnapshotRepository snapshots,
    IArtifactStore artifacts,
    IProviderProfileDefinitionEvaluator providerDefinitions,
    IProviderProfileValidator providerValidator,
    IAgentDefinitionEvaluator agentDefinitions,
    ILocalAdministratorAuthenticator authenticator,
    IAuditIntegrityVerifier auditIntegrity,
    IAuditReader auditReader,
    IAuditRecorder auditRecorder,
    IUnitOfWork unitOfWork,
    IClock clock,
    ISensitiveDataRedactor redactor) : ISetupProfileRestorer
{
    private const long MaximumRollbackBytes = 2 * 1024 * 1024;
    private const string RollbackMediaType = "application/vnd.agentforge.setup+json";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 64,
    };

    public async Task<DomainResult<SetupProfileRestorePreview>> PreviewAsync(
        PreviewSetupProfileRestoreRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = await PrepareAsync(
            request.SnapshotId,
            request.ExpectedInstallationVersion,
            request.ActorId,
            request.CorrelationId,
            request.AdministratorCredential,
            cancellationToken);
        return prepared.IsSuccess
            ? DomainResult.Success(new SetupProfileRestorePreview(
                prepared.Value.Snapshot,
                prepared.Value.Changes,
                prepared.Value.RequestHash))
            : DomainResult.Fail<SetupProfileRestorePreview>(prepared.Failure!);
    }

    public async Task<DomainResult<SetupProfileRestoreResult>> ApplyAsync(
        ApplySetupProfileRestoreRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hashFailure = ValidateHash(request.ExpectedRequestHash);
        if (hashFailure is not null)
        {
            return DomainResult.Fail<SetupProfileRestoreResult>(hashFailure);
        }

        var prepared = await PrepareAsync(
            request.SnapshotId,
            request.ExpectedInstallationVersion,
            request.ActorId,
            request.CorrelationId,
            request.AdministratorCredential,
            cancellationToken);
        if (!prepared.IsSuccess)
        {
            return DomainResult.Fail<SetupProfileRestoreResult>(prepared.Failure!);
        }

        if (!string.Equals(prepared.Value.RequestHash, request.ExpectedRequestHash, StringComparison.Ordinal))
        {
            return DomainResult.Fail<SetupProfileRestoreResult>(new DomainFailure(
                FailureCode.PolicyDenied,
                "Restore parameters do not match the authenticated preview hash."));
        }

        if (prepared.Value.Changes.Count == 0)
        {
            return DomainResult.Fail<SetupProfileRestoreResult>(new DomainFailure(
                FailureCode.ValidationFailure,
                "The rollback profile is already effective."));
        }

        var installationUpdate = InstallationStateMachine.Transition(
            prepared.Value.Installation,
            InstallationTrigger.ConfigurationChanged,
            NextTimestamp(prepared.Value.Installation.UpdatedAt),
            request.ActorId,
            request.CorrelationId);
        if (!installationUpdate.IsSuccess)
        {
            return DomainResult.Fail<SetupProfileRestoreResult>(installationUpdate.Failure!);
        }

        foreach (var provider in prepared.Value.Providers.Where(item => item.Changed))
        {
            await providers.UpdateAsync(provider.Effective, provider.Current.Version, cancellationToken);
        }

        foreach (var agent in prepared.Value.Agents.Where(item => item.Changed))
        {
            await agents.UpdateAsync(agent.Effective, agent.Current.Version, cancellationToken);
        }

        await installations.UpdateAsync(
            installationUpdate.Value,
            request.ExpectedInstallationVersion,
            cancellationToken);
        await auditRecorder.RecordAsync(new AuditRecordRequest(
            prepared.Value.Installation.Id,
            request.ActorId,
            request.CorrelationId,
            prepared.Value.Installation.CorrelationId,
            "setup.profile-restored",
            AuditOutcome.Succeeded,
            new
            {
                SnapshotId = request.SnapshotId.ToString(),
                request.ExpectedInstallationVersion,
                request.ExpectedRequestHash,
                SnapshotHash = prepared.Value.Snapshot.Artifact.ContentHash,
                ChangedPaths = prepared.Value.Changes.Select(item => item.Path).ToArray(),
            },
            new
            {
                InstallationVersion = installationUpdate.Value.Version,
                RestoredProviderCount = prepared.Value.Providers.Count(item => item.Changed),
                RestoredAgentCount = prepared.Value.Agents.Count(item => item.Changed),
            },
            null), cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded
            ? DomainResult.Success(new SetupProfileRestoreResult(
                installationUpdate.Value,
                prepared.Value.Snapshot,
                prepared.Value.Providers.Count(item => item.Changed),
                prepared.Value.Agents.Count(item => item.Changed),
                prepared.Value.Changes,
                prepared.Value.RequestHash))
            : DomainResult.Fail<SetupProfileRestoreResult>(commit.Failure!);
    }

    private async Task<DomainResult<RestorePreparation>> PrepareAsync(
        SetupProfileSnapshotId snapshotId,
        long expectedInstallationVersion,
        ActorId actorId,
        CorrelationId correlationId,
        ReadOnlyMemory<char> credential,
        CancellationToken cancellationToken)
    {
        if (snapshotId.Value == Guid.Empty)
        {
            return Invalid<RestorePreparation>("Rollback snapshot ID cannot be empty.");
        }

        var identityFailure = ValidateIdentity(actorId, correlationId);
        if (identityFailure is not null)
        {
            return DomainResult.Fail<RestorePreparation>(identityFailure);
        }

        var installation = await installations.ReadAsync(cancellationToken);
        if (installation.Id.Value == Guid.Empty)
        {
            return DomainResult.Fail<RestorePreparation>(new DomainFailure(
                FailureCode.InvalidStateTransition,
                "Installation is uninitialized."));
        }

        var authentication = await authenticator.AuthenticateAsync(
            installation.Id,
            credential,
            cancellationToken);
        if (!authentication.IsSuccess || authentication.Value != actorId)
        {
            return DomainResult.Fail<RestorePreparation>(new DomainFailure(
                FailureCode.PolicyDenied,
                "The authenticated administrator does not match the requested actor."));
        }

        if (installation.Version != expectedInstallationVersion)
        {
            return Conflict<RestorePreparation>("Installation version changed; refresh the restore preview.");
        }

        if (installation.State is not InstallationState.Configuring)
        {
            return DomainResult.Fail<RestorePreparation>(new DomainFailure(
                FailureCode.InvalidStateTransition,
                "Rollback profiles can be restored only while installation state is Configuring."));
        }

        var snapshot = await snapshots.FindByIdAsync(snapshotId, cancellationToken);
        if (snapshot is null || snapshot.InstallationId != installation.Id ||
            snapshot.Kind is not SetupProfileSnapshotKind.Rollback)
        {
            return Invalid<RestorePreparation>("Rollback snapshot does not belong to this installation.");
        }

        var integrity = await auditIntegrity.VerifyAsync(cancellationToken);
        if (!integrity.IsValid || !await HasAuditProvenanceAsync(snapshot, cancellationToken))
        {
            return DomainResult.Fail<RestorePreparation>(new DomainFailure(
                FailureCode.PolicyDenied,
                "Rollback snapshot does not have valid audit provenance."));
        }

        var documentResult = await ReadAndVerifyAsync(snapshot, cancellationToken);
        if (!documentResult.IsSuccess)
        {
            return DomainResult.Fail<RestorePreparation>(documentResult.Failure!);
        }

        var document = documentResult.Value;
        if (!string.Equals(document.DocumentType, "agentforge.rollback-profile", StringComparison.Ordinal) ||
            document.SchemaVersion != 1 ||
            document.Installation is null ||
            document.Providers is null ||
            document.Agents is null ||
            document.Administrator is null ||
            !string.Equals(document.Installation.Id, installation.Id.ToString(), StringComparison.Ordinal) ||
            document.Installation.Version != snapshot.ProfileVersion)
        {
            return Invalid<RestorePreparation>("Rollback artifact schema or installation binding is invalid.");
        }

        var administrator = await administrators.FindAsync(installation.Id, cancellationToken);
        if (administrator is null ||
            !string.Equals(document.Administrator.Id, administrator.Id.ToString(), StringComparison.Ordinal) ||
            !string.Equals(document.Administrator.ActorId, administrator.ActorId.Value, StringComparison.Ordinal) ||
            document.Administrator.ClientReference is null ||
            !string.Equals(document.Administrator.ClientReference.Store, administrator.ClientCredentialReference.Store, StringComparison.Ordinal) ||
            !string.Equals(document.Administrator.ClientReference.Key, administrator.ClientCredentialReference.Key, StringComparison.Ordinal))
        {
            return DomainResult.Fail<RestorePreparation>(new DomainFailure(
                FailureCode.PolicyDenied,
                "Rollback artifact administrator binding does not match the active installation."));
        }

        var currentProviders = await providers.ListAsync(installation.Id, cancellationToken);
        var providerRestores = await PrepareProvidersAsync(
            document.Providers,
            currentProviders,
            actorId,
            correlationId,
            cancellationToken);
        if (!providerRestores.IsSuccess)
        {
            return DomainResult.Fail<RestorePreparation>(providerRestores.Failure!);
        }

        var currentAgents = await agents.ListAsync(installation.Id, cancellationToken);
        var agentRestores = PrepareAgents(
            document.Agents,
            currentAgents,
            providerRestores.Value,
            installation,
            actorId,
            correlationId);
        if (!agentRestores.IsSuccess)
        {
            return DomainResult.Fail<RestorePreparation>(agentRestores.Failure!);
        }

        var changes = providerRestores.Value
            .Where(item => item.Changed)
            .Select(item => new SetupProfileChange(
                $"providers/{item.Current.Id}",
                item.CurrentHash,
                item.EffectiveHash))
            .Concat(agentRestores.Value
                .Where(item => item.Changed)
                .Select(item => new SetupProfileChange(
                    $"agents/{item.Current.Id}",
                    item.CurrentHash,
                    item.EffectiveHash)))
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();
        var requestHash = ComputeHash(new
        {
            Kind = "setup-profile-restore-v1",
            InstallationId = installation.Id.ToString(),
            expectedInstallationVersion,
            SnapshotId = snapshot.Id.ToString(),
            SnapshotHash = snapshot.Artifact.ContentHash,
            ActorId = actorId.Value,
            CorrelationId = correlationId.Value,
            Providers = providerRestores.Value.Select(item => new
            {
                Id = item.Current.Id.ToString(),
                CurrentVersion = item.Current.Version,
                item.EffectiveHash,
            }),
            Agents = agentRestores.Value.Select(item => new
            {
                Id = item.Current.Id.ToString(),
                CurrentVersion = item.Current.Version,
                item.EffectiveHash,
            }),
        });
        return DomainResult.Success(new RestorePreparation(
            installation,
            snapshot,
            providerRestores.Value,
            agentRestores.Value,
            changes,
            requestHash));
    }

    private async Task<DomainResult<IReadOnlyList<ProviderRestore>>> PrepareProvidersAsync(
        IReadOnlyList<RollbackProvider> desiredProviders,
        IReadOnlyList<ProviderProfile> currentProviders,
        ActorId actorId,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        if (desiredProviders.Count != currentProviders.Count)
        {
            return Invalid<IReadOnlyList<ProviderRestore>>(
                "Rollback provider topology differs from the active installation.");
        }

        if (desiredProviders.Any(item => item is null))
        {
            return Invalid<IReadOnlyList<ProviderRestore>>("Rollback provider entries cannot be null.");
        }

        var currentById = currentProviders.ToDictionary(item => item.Id);
        var restores = new List<ProviderRestore>(desiredProviders.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var identifiers = new HashSet<ProviderProfileId>();
        foreach (var desired in desiredProviders.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            if (!Guid.TryParseExact(desired.Id, "D", out var id) || id == Guid.Empty ||
                !identifiers.Add(new ProviderProfileId(id)) ||
                !currentById.TryGetValue(new ProviderProfileId(id), out var current) ||
                desired.SecretReference is null)
            {
                return Invalid<IReadOnlyList<ProviderRestore>>(
                    "Rollback provider identity or topology is invalid.");
            }

            var candidate = new ProviderProfileCandidate(
                desired.Name,
                desired.ProviderType,
                desired.Endpoint,
                desired.Model,
                new SecretReference(desired.SecretReference.Store, desired.SecretReference.Key));
            var normalized = providerDefinitions.NormalizeAndValidate(candidate);
            if (!normalized.IsSuccess)
            {
                return DomainResult.Fail<IReadOnlyList<ProviderRestore>>(normalized.Failure!);
            }

            if (!names.Add(normalized.Value.Name))
            {
                return Invalid<IReadOnlyList<ProviderRestore>>("Rollback provider names are not unique.");
            }

            var capabilities = await providerValidator.ValidateAsync(normalized.Value, cancellationToken);
            if (!capabilities.IsSuccess)
            {
                return DomainResult.Fail<IReadOnlyList<ProviderRestore>>(capabilities.Failure!);
            }

            var currentHash = ComputeHash(new
            {
                Candidate = ToCandidate(current),
                current.Capabilities,
            });
            var effectiveHash = ComputeHash(new
            {
                Candidate = normalized.Value,
                Capabilities = capabilities.Value,
            });
            var changed = !string.Equals(currentHash, effectiveHash, StringComparison.Ordinal);
            var effective = changed
                ? current with
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
                }
                : current;
            restores.Add(new ProviderRestore(current, effective, currentHash, effectiveHash, changed));
        }

        return DomainResult.Success<IReadOnlyList<ProviderRestore>>(restores);
    }

    private DomainResult<IReadOnlyList<AgentRestore>> PrepareAgents(
        IReadOnlyList<AgentIdentity> desiredAgents,
        IReadOnlyList<AgentIdentity> currentAgents,
        IReadOnlyList<ProviderRestore> providerRestores,
        InstallationSnapshot installation,
        ActorId actorId,
        CorrelationId correlationId)
    {
        if (desiredAgents.Count != currentAgents.Count)
        {
            return Invalid<IReadOnlyList<AgentRestore>>(
                "Rollback agent topology differs from the active installation.");
        }

        if (desiredAgents.Any(item => item is null))
        {
            return Invalid<IReadOnlyList<AgentRestore>>("Rollback agent entries cannot be null.");
        }

        var currentById = currentAgents.ToDictionary(item => item.Id);
        var providerById = providerRestores.ToDictionary(item => item.Effective.Id, item => item.Effective);
        var restores = new List<AgentRestore>(desiredAgents.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var identifiers = new HashSet<AgentIdentityId>();
        foreach (var desired in desiredAgents.OrderBy(item => item.Id.Value))
        {
            if (desired.Id.Value == Guid.Empty || !identifiers.Add(desired.Id) || desired.InstallationId != installation.Id ||
                !currentById.TryGetValue(desired.Id, out var current))
            {
                return Invalid<IReadOnlyList<AgentRestore>>("Rollback agent identity or topology is invalid.");
            }

            var normalized = agentDefinitions.NormalizeAndValidate(ToCandidate(desired));
            if (!normalized.IsSuccess)
            {
                return DomainResult.Fail<IReadOnlyList<AgentRestore>>(normalized.Failure!);
            }

            if (!names.Add(normalized.Value.Name) ||
                !providerById.TryGetValue(normalized.Value.ModelPolicy.PrimaryProviderProfileId, out var provider))
            {
                return Invalid<IReadOnlyList<AgentRestore>>(
                    "Rollback agent names or provider bindings are invalid.");
            }

            var effectiveDefinition = agentDefinitions.Evaluate(normalized.Value, provider);
            if (!effectiveDefinition.IsSuccess)
            {
                return DomainResult.Fail<IReadOnlyList<AgentRestore>>(effectiveDefinition.Failure!);
            }

            var currentHash = ComputeHash(ToCandidate(current));
            var effectiveHash = ComputeHash(effectiveDefinition.Value.Agent);
            var changed = !string.Equals(currentHash, effectiveHash, StringComparison.Ordinal);
            var definition = effectiveDefinition.Value.Agent;
            var effective = changed
                ? current with
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
                }
                : current;
            restores.Add(new AgentRestore(current, effective, currentHash, effectiveHash, changed));
        }

        return DomainResult.Success<IReadOnlyList<AgentRestore>>(restores);
    }

    private async Task<DomainResult<RollbackProfileDocument>> ReadAndVerifyAsync(
        SetupProfileSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.Artifact.Length is < 1 or > MaximumRollbackBytes ||
            !string.Equals(snapshot.Artifact.MediaType, RollbackMediaType, StringComparison.Ordinal))
        {
            return Invalid<RollbackProfileDocument>("Rollback artifact metadata is invalid.");
        }

        try
        {
            await using var stream = await artifacts.OpenReadAsync(snapshot.Artifact, cancellationToken);
            using var content = new MemoryStream((int)snapshot.Artifact.Length);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total = checked(total + read);
                if (total > snapshot.Artifact.Length || total > MaximumRollbackBytes)
                {
                    return Invalid<RollbackProfileDocument>("Rollback artifact length is invalid.");
                }

                await content.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (total != snapshot.Artifact.Length)
            {
                return Invalid<RollbackProfileDocument>("Rollback artifact length is invalid.");
            }

            var bytes = content.ToArray();
            var contentHash = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
            if (!string.Equals(contentHash, snapshot.Artifact.ContentHash, StringComparison.Ordinal))
            {
                return DomainResult.Fail<RollbackProfileDocument>(new DomainFailure(
                    FailureCode.PolicyDenied,
                    "Rollback artifact content hash verification failed."));
            }

            var document = JsonSerializer.Deserialize<RollbackProfileDocument>(bytes, SerializerOptions);
            return document is null
                ? Invalid<RollbackProfileDocument>("Rollback artifact JSON is empty.")
                : DomainResult.Success(document);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or OverflowException)
        {
            return DomainResult.Fail<RollbackProfileDocument>(new DomainFailure(
                FailureCode.RecoverableExternalFailure,
                "Rollback artifact could not be read and verified.",
                IsRetryable: exception is IOException));
        }
    }

    private async Task<bool> HasAuditProvenanceAsync(
        SetupProfileSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        long afterSequence = 0;
        for (var page = 0; page < 10; page++)
        {
            var events = await auditReader.ReadAsync(
                snapshot.InstallationId,
                afterSequence,
                1000,
                cancellationToken);
            foreach (var auditEvent in events)
            {
                if (auditEvent.CorrelationId != snapshot.CorrelationId ||
                    auditEvent.OperationType is not ("setup.profile-exported" or "setup.recovery-entered"))
                {
                    continue;
                }

                using var output = JsonDocument.Parse(auditEvent.Output.Json);
                if (output.RootElement.TryGetProperty("rollbackHash", out var hash) &&
                    string.Equals(hash.GetString(), snapshot.Artifact.ContentHash, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            if (events.Count < 1000)
            {
                return false;
            }

            afterSequence = events[^1].Sequence;
        }

        return false;
    }

    private DomainFailure? ValidateIdentity(ActorId actorId, CorrelationId correlationId)
    {
        if (string.IsNullOrWhiteSpace(actorId.Value) || actorId.Value.Length > 256 || actorId.Value.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(correlationId.Value) || correlationId.Value.Length > 128 || correlationId.Value.Any(char.IsControl))
        {
            return new DomainFailure(
                FailureCode.ValidationFailure,
                "Actor and correlation IDs must be bounded printable values.");
        }

        return redactor.Redact(new[] { actorId.Value, correlationId.Value }).ContainsRedactions
            ? new DomainFailure(
                FailureCode.ValidationFailure,
                "Actor and correlation IDs cannot contain credential-shaped content.")
            : null;
    }

    private DateTimeOffset NextTimestamp(DateTimeOffset current) =>
        current < clock.UtcNow ? clock.UtcNow : current.AddTicks(1);

    private static ProviderProfileCandidate ToCandidate(ProviderProfile profile) => new(
        profile.Name,
        profile.ProviderType,
        profile.Endpoint,
        profile.Model,
        profile.SecretReference);

    private static AgentIdentityCandidate ToCandidate(AgentIdentity agent) => new(
        agent.Name,
        agent.Expertise,
        agent.Mission,
        agent.PreferredLanguage,
        agent.TimeZone,
        agent.ResponseStyle,
        agent.DefaultWorkspace,
        agent.ModelPolicy,
        agent.MemoryPolicy,
        agent.CapabilityPolicy,
        agent.Budget,
        agent.ChildLimits,
        agent.LearningPolicy);

    private static string ComputeHash(object value) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions)));

    private static DomainFailure? ValidateHash(string value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit) &&
        value.All(character => !char.IsAsciiLetter(character) || char.IsLower(character))
            ? null
            : new DomainFailure(
                FailureCode.ValidationFailure,
                "Expected preview hash must be 64 lowercase hexadecimal characters.");

    private static DomainResult<T> Invalid<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<T> Conflict<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ConcurrencyConflict, message, IsRetryable: true));

    private sealed record RestorePreparation(
        InstallationSnapshot Installation,
        SetupProfileSnapshot Snapshot,
        IReadOnlyList<ProviderRestore> Providers,
        IReadOnlyList<AgentRestore> Agents,
        IReadOnlyList<SetupProfileChange> Changes,
        string RequestHash);

    private sealed record ProviderRestore(
        ProviderProfile Current,
        ProviderProfile Effective,
        string CurrentHash,
        string EffectiveHash,
        bool Changed);

    private sealed record AgentRestore(
        AgentIdentity Current,
        AgentIdentity Effective,
        string CurrentHash,
        string EffectiveHash,
        bool Changed);

    private sealed record RollbackProfileDocument(
        string? DocumentType,
        int SchemaVersion,
        RollbackInstallation? Installation,
        IReadOnlyList<RollbackProvider>? Providers,
        IReadOnlyList<AgentIdentity>? Agents,
        RollbackAdministrator? Administrator);

    private sealed record RollbackInstallation(
        string? Id,
        string? State,
        long Version);

    private sealed record RollbackProvider(
        string Id,
        string Name,
        string ProviderType,
        Uri Endpoint,
        string Model,
        RollbackSecretReference? SecretReference,
        ProviderCapabilitySummary? Capabilities,
        long Version);

    private sealed record RollbackSecretReference(
        string Store,
        string Key);

    private sealed record RollbackAdministrator(
        string? Id,
        string? ActorId,
        RollbackSecretReference? ClientReference,
        long Version);
}
