using System.Text;
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
using AgentForge.Domain.Artifacts;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Security;
using AgentForge.Domain.Setup;

namespace AgentForge.Setup;

internal sealed class SetupMaintenanceService(
    IInstallationRepository installations,
    IProviderProfileRepository providers,
    IAgentIdentityRepository agents,
    ILocalAdministratorRepository administrators,
    ISetupProfileSnapshotRepository snapshots,
    IAuditIntegrityVerifier auditIntegrity,
    IAuditRecorder auditRecorder,
    IArtifactStore artifacts,
    ISecretStore secretStore,
    ISensitiveDataRedactor redactor,
    ILocalAdministratorAuthenticator authenticator,
    IDataDirectoryProvider dataDirectoryProvider,
    IUnitOfWork unitOfWork,
    IClock clock,
    IIdentifierGenerator identifiers) : ISetupMaintenanceService
{
    public async Task<DomainResult<SetupDoctorReport>> DoctorAsync(
        DoctorRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestFailure = ValidateIdentity(request.ActorId, request.CorrelationId);
        if (requestFailure is not null)
        {
            return DomainResult.Fail<SetupDoctorReport>(requestFailure);
        }

        var installation = await installations.ReadAsync(cancellationToken);
        var checks = new List<DoctorCheck>();
        checks.Add(await CheckStorageAsync(cancellationToken));

        var audit = await auditIntegrity.VerifyAsync(cancellationToken);
        checks.Add(new DoctorCheck(
            "audit.integrity",
            audit.IsValid ? DoctorCheckStatus.Pass : DoctorCheckStatus.Fail,
            audit.IsValid
                ? $"Verified {audit.VerifiedEventCount} audit event(s)."
                : $"Audit verification failed at sequence {audit.BrokenSequence}."));

        var secretCapability = secretStore.GetCapability();
        checks.Add(new DoctorCheck(
            "secret-store.capability",
            secretCapability.IsAvailable ? DoctorCheckStatus.Pass : DoctorCheckStatus.Fail,
            secretCapability.IsAvailable
                ? $"Secret store '{secretCapability.Store}' is available."
                : "The configured OS secret store is unavailable."));

        if (installation.Id.Value == Guid.Empty)
        {
            checks.Add(new DoctorCheck("installation.state", DoctorCheckStatus.Fail, "Installation is uninitialized."));
            return DomainResult.Success(new SetupDoctorReport(clock.UtcNow, installation, checks));
        }

        checks.Add(new DoctorCheck(
            "installation.state",
            installation.State is InstallationState.Ready
                ? DoctorCheckStatus.Pass
                : installation.State is InstallationState.RecoveryRequired
                    ? DoctorCheckStatus.Fail
                    : DoctorCheckStatus.Warning,
            $"Installation state is {installation.State}."));

        var providerProfiles = await providers.ListAsync(installation.Id, cancellationToken);
        var textProviders = providerProfiles.Where(item => item.Capabilities.TextGeneration).ToArray();
        checks.Add(new DoctorCheck(
            "provider.text",
            textProviders.Length > 0 ? DoctorCheckStatus.Pass : DoctorCheckStatus.Fail,
            textProviders.Length > 0
                ? $"Found {textProviders.Length} validated text provider(s)."
                : "No validated text provider is configured."));

        var materializableCount = 0;
        if (secretCapability.IsAvailable)
        {
            foreach (var provider in textProviders)
            {
                var materialized = await secretStore.MaterializeAsync(provider.SecretReference, cancellationToken);
                if (materialized.IsSuccess)
                {
                    materializableCount++;
                    await materialized.Value.DisposeAsync();
                }
            }
        }

        checks.Add(new DoctorCheck(
            "provider.secrets",
            materializableCount == textProviders.Length && textProviders.Length > 0
                ? DoctorCheckStatus.Pass
                : DoctorCheckStatus.Fail,
            $"Materialized {materializableCount} of {textProviders.Length} usable provider reference(s)."));

        var configuredAgents = await agents.ListAsync(installation.Id, cancellationToken);
        checks.Add(new DoctorCheck(
            "agent.identity",
            configuredAgents.Count > 0 ? DoctorCheckStatus.Pass : DoctorCheckStatus.Fail,
            configuredAgents.Count > 0
                ? $"Found {configuredAgents.Count} named agent(s)."
                : "No named agent is configured."));

        var administrator = await administrators.FindAsync(installation.Id, cancellationToken);
        var administratorRequired = installation.State is InstallationState.Ready or InstallationState.RecoveryRequired;
        checks.Add(new DoctorCheck(
            "administrator.local",
            administrator is not null
                ? DoctorCheckStatus.Pass
                : administratorRequired ? DoctorCheckStatus.Fail : DoctorCheckStatus.Warning,
            administrator is not null
                ? "A local administrator verifier and OS reference are configured."
                : "No local administrator is configured."));

        return DomainResult.Success(new SetupDoctorReport(clock.UtcNow, installation, checks));
    }

    public async Task<DomainResult<ExportSetupProfileResult>> ExportAsync(
        ExportSetupProfileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var installation = await installations.ReadAsync(cancellationToken);
        var authorization = await AuthorizeAsync(
            installation,
            request.ExpectedInstallationVersion,
            request.ActorId,
            request.CorrelationId,
            request.AdministratorCredential,
            cancellationToken);
        if (!authorization.IsSuccess)
        {
            return DomainResult.Fail<ExportSetupProfileResult>(authorization.Failure!);
        }

        var doctor = await DoctorAsync(
            new DoctorRequest(request.ActorId, request.CorrelationId),
            cancellationToken);
        if (!doctor.IsSuccess)
        {
            return DomainResult.Fail<ExportSetupProfileResult>(doctor.Failure!);
        }

        var providerProfiles = await providers.ListAsync(installation.Id, cancellationToken);
        var configuredAgents = await agents.ListAsync(installation.Id, cancellationToken);
        var administrator = await administrators.FindAsync(installation.Id, cancellationToken)
            ?? throw new InvalidOperationException("Authorized installation has no administrator record.");
        var generatedAt = clock.UtcNow;

        var reportPayload = new
        {
            DocumentType = "agentforge.setup-report",
            SchemaVersion = 1,
            GeneratedAt = generatedAt,
            InstallationId = installation.Id.ToString(),
            InstallationState = installation.State.ToString(),
            ProfileVersion = installation.Version,
            Checks = doctor.Value.Checks,
            ProviderCount = providerProfiles.Count,
            AgentCount = configuredAgents.Count,
        };
        var redactedReport = redactor.Redact(reportPayload);
        var reportArtifact = await PutJsonAsync(redactedReport.Data.Json, cancellationToken);
        var reportSnapshot = CreateSnapshot(
            installation,
            SetupProfileSnapshotKind.SetupReport,
            reportArtifact,
            request.ActorId,
            request.CorrelationId,
            generatedAt);
        var rollback = await CreateRollbackSnapshotAsync(
            installation,
            providerProfiles,
            configuredAgents,
            administrator,
            request.ActorId,
            request.CorrelationId,
            generatedAt,
            cancellationToken);
        await snapshots.AddAsync(reportSnapshot, cancellationToken);
        // Mark the unchanged row with its original concurrency token so the export,
        // snapshot metadata, and audit cannot commit against a version that changed
        // after authorization.
        await installations.UpdateAsync(
            installation,
            request.ExpectedInstallationVersion,
            cancellationToken);

        await auditRecorder.RecordAsync(new AuditRecordRequest(
            installation.Id,
            request.ActorId,
            request.CorrelationId,
            installation.CorrelationId,
            "setup.profile-exported",
            AuditOutcome.Succeeded,
            new { request.ExpectedInstallationVersion },
            new
            {
                ReportHash = reportArtifact.ContentHash,
                RollbackHash = rollback.Snapshot.Artifact.ContentHash,
                RedactionCount = redactedReport.RedactionCount + rollback.RedactionCount,
            },
            null), cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded
            ? DomainResult.Success(new ExportSetupProfileResult(
                reportSnapshot,
                rollback.Snapshot,
                redactedReport.RedactionCount + rollback.RedactionCount))
            : DomainResult.Fail<ExportSetupProfileResult>(commit.Failure!);
    }

    public Task<DomainResult<RecoveryTransitionResult>> EnterRecoveryAsync(
        EnterRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2048 || request.Reason.Any(char.IsControl))
        {
            return Task.FromResult(DomainResult.Fail<RecoveryTransitionResult>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Recovery reason must contain 1 to 2048 printable characters.")));
        }

        if (redactor.Redact(request.Reason).ContainsRedactions)
        {
            return Task.FromResult(DomainResult.Fail<RecoveryTransitionResult>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Recovery reason cannot contain credential-shaped content.")));
        }

        return TransitionRecoveryAsync(
            request.ExpectedInstallationVersion,
            InstallationTrigger.StartRecovery,
            request.Reason.Trim(),
            request.ActorId,
            request.CorrelationId,
            request.AdministratorCredential,
            "setup.recovery-entered",
            cancellationToken);
    }

    public Task<DomainResult<RecoveryTransitionResult>> ResumeRecoveryAsync(
        ResumeRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return TransitionRecoveryAsync(
            request.ExpectedInstallationVersion,
            InstallationTrigger.ResumeConfiguration,
            null,
            request.ActorId,
            request.CorrelationId,
            request.AdministratorCredential,
            "setup.recovery-resumed",
            cancellationToken);
    }

    private async Task<DomainResult<RecoveryTransitionResult>> TransitionRecoveryAsync(
        long expectedVersion,
        InstallationTrigger trigger,
        string? reason,
        ActorId actorId,
        CorrelationId correlationId,
        ReadOnlyMemory<char> credential,
        string operation,
        CancellationToken cancellationToken)
    {
        var installation = await installations.ReadAsync(cancellationToken);
        var authorization = await AuthorizeAsync(
            installation,
            expectedVersion,
            actorId,
            correlationId,
            credential,
            cancellationToken);
        if (!authorization.IsSuccess)
        {
            return DomainResult.Fail<RecoveryTransitionResult>(authorization.Failure!);
        }

        var transition = InstallationStateMachine.Transition(
            installation,
            trigger,
            clock.UtcNow,
            actorId,
            correlationId,
            reason);
        if (!transition.IsSuccess)
        {
            return DomainResult.Fail<RecoveryTransitionResult>(transition.Failure!);
        }

        SetupProfileSnapshot? rollbackSnapshot = null;
        if (trigger is InstallationTrigger.StartRecovery)
        {
            var providerProfiles = await providers.ListAsync(installation.Id, cancellationToken);
            var configuredAgents = await agents.ListAsync(installation.Id, cancellationToken);
            var administrator = await administrators.FindAsync(installation.Id, cancellationToken)
                ?? throw new InvalidOperationException("Authorized installation has no administrator record.");
            rollbackSnapshot = (await CreateRollbackSnapshotAsync(
                installation,
                providerProfiles,
                configuredAgents,
                administrator,
                actorId,
                correlationId,
                clock.UtcNow,
                cancellationToken)).Snapshot;
        }

        await installations.UpdateAsync(transition.Value, expectedVersion, cancellationToken);
        await auditRecorder.RecordAsync(new AuditRecordRequest(
            installation.Id,
            actorId,
            correlationId,
            installation.CorrelationId,
            operation,
            AuditOutcome.Succeeded,
            new { PreviousState = installation.State.ToString(), expectedVersion, Reason = reason },
            new
            {
                State = transition.Value.State.ToString(),
                transition.Value.Version,
                RollbackHash = rollbackSnapshot?.Artifact.ContentHash,
            },
            null), cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded
            ? DomainResult.Success(new RecoveryTransitionResult(transition.Value, rollbackSnapshot))
            : DomainResult.Fail<RecoveryTransitionResult>(commit.Failure!);
    }

    private async Task<DomainResult<ActorId>> AuthorizeAsync(
        InstallationSnapshot installation,
        long expectedVersion,
        ActorId actorId,
        CorrelationId correlationId,
        ReadOnlyMemory<char> credential,
        CancellationToken cancellationToken)
    {
        var identityFailure = ValidateIdentity(actorId, correlationId);
        if (identityFailure is not null)
        {
            return DomainResult.Fail<ActorId>(identityFailure);
        }

        if (installation.Id.Value == Guid.Empty)
        {
            return DomainResult.Fail<ActorId>(new DomainFailure(
                FailureCode.InvalidStateTransition,
                "Installation is uninitialized."));
        }

        var authentication = await authenticator.AuthenticateAsync(
            installation.Id,
            credential,
            cancellationToken);
        if (!authentication.IsSuccess || authentication.Value != actorId)
        {
            return DomainResult.Fail<ActorId>(new DomainFailure(
                FailureCode.PolicyDenied,
                "The authenticated administrator does not match the requested actor."));
        }

        if (installation.Version != expectedVersion)
        {
            return DomainResult.Fail<ActorId>(new DomainFailure(
                FailureCode.ConcurrencyConflict,
                "Installation version changed; refresh the profile before retrying.",
                IsRetryable: true));
        }

        return authentication;
    }

    private async Task<DoctorCheck> CheckStorageAsync(CancellationToken cancellationToken)
    {
        string? temporaryPath = null;
        try
        {
            var dataDirectory = Path.GetFullPath(dataDirectoryProvider.GetDataDirectory());
            Directory.CreateDirectory(dataDirectory);
            temporaryPath = Path.Combine(dataDirectory, $".doctor-{identifiers.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(temporaryPath, [0x41, 0x46], cancellationToken);
            await using var stream = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None,
                2,
                FileOptions.Asynchronous);
            var buffer = new byte[2];
            var read = await stream.ReadAsync(buffer, cancellationToken);
            return new DoctorCheck(
                "storage.read-write-lock",
                read == 2 && buffer is [0x41, 0x46] ? DoctorCheckStatus.Pass : DoctorCheckStatus.Fail,
                read == 2 ? "Data directory supports bounded write, exclusive lock, and read." : "Data directory probe was incomplete.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new DoctorCheck(
                "storage.read-write-lock",
                DoctorCheckStatus.Fail,
                "Data directory write/lock/read probe failed.");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDeleteProbe(temporaryPath);
            }
        }
    }

    private static void TryDeleteProbe(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The diagnostic result must not be replaced by a best-effort cleanup failure.
        }
    }

    private async Task<ArtifactReference> PutJsonAsync(
        string json,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await using var stream = new MemoryStream(bytes, writable: false);
        return await artifacts.PutAsync(stream, "application/vnd.agentforge.setup+json", cancellationToken);
    }

    private async Task<(SetupProfileSnapshot Snapshot, int RedactionCount)> CreateRollbackSnapshotAsync(
        InstallationSnapshot installation,
        IReadOnlyList<ProviderProfile> providerProfiles,
        IReadOnlyList<AgentIdentity> configuredAgents,
        LocalAdministrator administrator,
        ActorId actorId,
        CorrelationId correlationId,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        var profilePayload = new
        {
            DocumentType = "agentforge.rollback-profile",
            SchemaVersion = 1,
            GeneratedAt = generatedAt,
            Installation = new
            {
                Id = installation.Id.ToString(),
                State = installation.State.ToString(),
                installation.Version,
                installation.UpdatedAt,
                ActorId = installation.ActorId.Value,
                CorrelationId = installation.CorrelationId.Value,
                installation.RecoveryReason,
            },
            Providers = providerProfiles.Select(item => new
            {
                Id = item.Id.ToString(),
                item.Name,
                item.ProviderType,
                Endpoint = item.Endpoint.AbsoluteUri,
                item.Model,
                SecretReference = new { item.SecretReference.Store, item.SecretReference.Key },
                item.Capabilities,
                item.Version,
            }).ToArray(),
            Agents = configuredAgents,
            Administrator = new
            {
                Id = administrator.Id.ToString(),
                ActorId = administrator.ActorId.Value,
                ClientReference = new
                {
                    administrator.ClientCredentialReference.Store,
                    administrator.ClientCredentialReference.Key,
                },
                administrator.Version,
            },
        };
        var redactedProfile = redactor.Redact(profilePayload);
        var artifact = await PutJsonAsync(redactedProfile.Data.Json, cancellationToken);
        var snapshot = CreateSnapshot(
            installation,
            SetupProfileSnapshotKind.Rollback,
            artifact,
            actorId,
            correlationId,
            generatedAt);
        await snapshots.AddAsync(snapshot, cancellationToken);
        return (snapshot, redactedProfile.RedactionCount);
    }

    private SetupProfileSnapshot CreateSnapshot(
        InstallationSnapshot installation,
        SetupProfileSnapshotKind kind,
        ArtifactReference artifact,
        ActorId actorId,
        CorrelationId correlationId,
        DateTimeOffset createdAt) => new(
            new SetupProfileSnapshotId(identifiers.NewGuid()),
            installation.Id,
            installation.Version,
            kind,
            artifact,
            createdAt,
            actorId,
            correlationId);

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

        if (redactor.Redact(new[] { actorId.Value, correlationId.Value }).ContainsRedactions)
        {
            return new DomainFailure(
                FailureCode.ValidationFailure,
                "Actor and correlation IDs cannot contain credential-shaped content.");
        }

        return null;
    }
}
