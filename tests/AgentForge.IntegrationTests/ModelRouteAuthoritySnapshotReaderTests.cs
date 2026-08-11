using System.Text;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Time;
using AgentForge.Audit;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Security;
using AgentForge.Models;
using AgentForge.Persistence;
using AgentForge.Security;
using AgentForge.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.IntegrationTests;

public sealed class ModelRouteAuthoritySnapshotReaderTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 16, 0, 0, TimeSpan.Zero);
    private static readonly InstallationId InstallationId = new(
        Guid.Parse("e45adf55-18dd-45f2-9064-7c5c2b757b05"));
    private static readonly AgentIdentityId AgentId = new(
        Guid.Parse("60992f5f-a83b-4734-8d98-0151a251f92b"));
    private static readonly ProviderProfileId ProviderId = new(
        Guid.Parse("861745e5-7408-4310-879c-2d8fa97c043f"));
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"agentforge-route-authority-{Guid.NewGuid():N}");
    private ServiceProvider? _services;
    private RecordingProvider? _provider;

    [Fact]
    public async Task Reads_installation_agent_and_provider_profiles_in_one_durable_snapshot()
    {
        await SeedAsync();
        await using var scope = Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<IModelRouteAuthoritySnapshotReader>();

        var result = await reader.ReadAsync(InstallationId, AgentId, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(InstallationState.Ready, result.Value.Installation.State);
        Assert.Equal(7, result.Value.Installation.Version);
        Assert.Equal(AgentId, result.Value.Agent.Id);
        Assert.Equal(3, result.Value.Agent.Version);
        var profile = Assert.Single(result.Value.ProviderProfiles);
        Assert.Equal(ProviderId, profile.Id);
        Assert.Equal(4, profile.Version);
        Assert.Equal("authority-model", profile.Model);
    }

    [Fact]
    public async Task Wrong_installation_or_agent_identity_returns_fixed_policy_denial()
    {
        await SeedAsync();
        await using var scope = Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<IModelRouteAuthoritySnapshotReader>();

        var wrongInstallation = await reader.ReadAsync(
            new InstallationId(Guid.Parse("2b33a69e-a9f7-45a8-b7dc-a9e77c47ed13")),
            AgentId,
            CancellationToken.None);
        var wrongAgent = await reader.ReadAsync(
            InstallationId,
            new AgentIdentityId(Guid.Parse("790e2264-a5e1-4dc9-8062-ad8d71cb90b2")),
            CancellationToken.None);

        Assert.Equal(FailureCode.PolicyDenied, wrongInstallation.Failure?.Code);
        Assert.Equal(FailureCode.PolicyDenied, wrongAgent.Failure?.Code);
        Assert.Equal(wrongInstallation.Failure?.Message, wrongAgent.Failure?.Message);
    }

    [Fact]
    public async Task Scoped_planner_uses_durable_authority_prepared_context_and_current_health()
    {
        await SeedAsync();
        await using var scope = Services.CreateAsyncScope();
        var planner = scope.ServiceProvider.GetRequiredService<IModelRoutePlanner>();
        var request = new ModelRequest(
            new ModelRequestId(Guid.Parse("60cc0ea7-3c76-4516-9201-bdd4ab454900")),
            "authority-model",
            [new ModelMessage(ModelMessageRole.User, [new ModelTextContent("safe integration input")])],
            [],
            new ModelResponseFormat(ModelResponseFormatKind.Text),
            new ModelInvocationLimits(100, 0, 32, 30),
            0,
            1,
            42,
            new CorrelationId("authority-plan"));

        var result = await planner.PlanAsync(new ModelRoutePlanningRequest(
            InstallationId,
            7,
            AgentId,
            3,
            request,
            100,
            []), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(ProviderId, result.Value.Route.ProfileId);
        Assert.Equal(4, result.Value.ProviderVersion);
        Assert.Equal(ModelContextPreparer.PolicyName, result.Value.ContextPreparationPolicy);
        Assert.Equal(71, result.Value.PlanEvidenceHash.Length);
    }

    [Fact]
    public async Task Model_run_admission_reserves_atomically_and_replays_without_prompt_persistence()
    {
        await SeedAsync();
        const string prompt = "unique admission prompt that must never enter durable storage";
        const string credential = "sk-" + "1234567890abcdefghijklmnop";
        var admission = Admission($"{prompt}; password={credential}", "admission-001");

        ModelRunAdmissionResult first;
        await using (var scope = Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IModelRunAdmissionService>()
                .AdmitAsync(admission, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Failure?.Message);
            first = result.Value;
            Assert.False(first.IsIdempotentReplay);
            Assert.Equal(ModelRunState.Reserved, first.Aggregate.Run.State);
            Assert.Equal(ModelRunAttemptState.Planned, first.Aggregate.Attempt.State);
            Assert.True(first.Aggregate.Run.ContextRedactionCount >= 1);
            Assert.Equal(200, first.Aggregate.Run.Reservation.InputTokens);
            Assert.Equal(100, first.Aggregate.Run.Reservation.OutputTokens);
        }

        await using (var replayScope = Services.CreateAsyncScope())
        {
            var replay = await replayScope.ServiceProvider.GetRequiredService<IModelRunAdmissionService>()
                .AdmitAsync(admission, CancellationToken.None);
            Assert.True(replay.IsSuccess, replay.Failure?.Message);
            Assert.True(replay.Value.IsIdempotentReplay);
            Assert.Equal(first.Aggregate.Run.Id, replay.Value.Aggregate.Run.Id);

            var persisted = await replayScope.ServiceProvider.GetRequiredService<IModelRunRepository>()
                .FindByIdAsync(first.Aggregate.Run.Id, CancellationToken.None);
            Assert.NotNull(persisted);
            Assert.Equal(first.Aggregate.Run.Id, persisted.Run.Id);
            Assert.Equal(first.Aggregate.Attempt.Id, persisted.Attempt.Id);
            Assert.Equal(first.Aggregate.Run.AdmissionRequestHash, persisted.Run.AdmissionRequestHash);
            Assert.Equal(
                first.Aggregate.Run.Route.RequiredCapabilities.OrderBy(item => item),
                persisted.Run.Route.RequiredCapabilities.OrderBy(item => item));
            var audit = Assert.Single(await replayScope.ServiceProvider.GetRequiredService<IAuditReader>()
                .ReadAsync(InstallationId, 0, 10, CancellationToken.None));
            Assert.Equal("model.run-reserved", audit.OperationType);
            Assert.Contains(first.Aggregate.Run.PlanEvidenceHash, audit.Input.Json, StringComparison.Ordinal);
            Assert.True((await replayScope.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>()
                .VerifyAsync(CancellationToken.None)).IsValid);
        }

        var databaseBytes = await File.ReadAllBytesAsync(Path.Combine(_directory, "agentforge.db"));
        Assert.Equal(-1, databaseBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(prompt)));
        Assert.Equal(-1, databaseBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(credential)));
    }

    [Fact]
    public async Task Model_run_idempotency_conflict_and_planning_failure_write_nothing_new()
    {
        await SeedAsync();
        await using var scope = Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IModelRunAdmissionService>();
        var first = await service.AdmitAsync(Admission("first request", "admission-conflict"), CancellationToken.None);
        Assert.True(first.IsSuccess, first.Failure?.Message);

        var conflict = await service.AdmitAsync(
            Admission("changed request", "admission-conflict"),
            CancellationToken.None);
        var deniedPlan = await service.AdmitAsync(
            Admission("wrong authority", "admission-denied") with
            {
                PlanningRequest = Admission("wrong authority", "admission-denied").PlanningRequest with
                {
                    ExpectedAgentVersion = 999,
                },
            },
            CancellationToken.None);

        Assert.Equal(FailureCode.ConcurrencyConflict, conflict.Failure?.Code);
        Assert.Equal(FailureCode.ConcurrencyConflict, deniedPlan.Failure?.Code);
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IModelRunRepository>()
            .FindByIdempotencyKeyAsync(InstallationId, "admission-denied", CancellationToken.None));
        Assert.Single(await scope.ServiceProvider.GetRequiredService<IAuditReader>()
            .ReadAsync(InstallationId, 0, 10, CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_exact_admissions_converge_on_one_run_and_one_audit_event()
    {
        await SeedAsync();
        var admission = Admission("concurrent exact request", "admission-race");

        var results = await Task.WhenAll(AdmitInNewScopeAsync(admission), AdmitInNewScopeAsync(admission));

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Failure?.Message));
        Assert.Single(results.Select(item => item.Value.Aggregate.Run.Id).Distinct());
        Assert.Single(results, item => !item.Value.IsIdempotentReplay);
        await using var verificationScope = Services.CreateAsyncScope();
        Assert.Single(await verificationScope.ServiceProvider.GetRequiredService<IAuditReader>()
            .ReadAsync(InstallationId, 0, 10, CancellationToken.None));
    }

    [Fact]
    public async Task Sensitive_metadata_correlation_mismatch_and_precancellation_are_write_free()
    {
        await SeedAsync();
        await using var scope = Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IModelRunAdmissionService>();
        var sensitive = await service.AdmitAsync(
            Admission("safe request", "password=must-not-be-metadata"),
            CancellationToken.None);
        var mismatch = Admission("safe request", "correlation-mismatch") with
        {
            CorrelationId = new CorrelationId("different-correlation"),
        };
        var mismatched = await service.AdmitAsync(mismatch, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.AdmitAsync(Admission("safe request", "precanceled"), cancellation.Token));

        Assert.Equal(FailureCode.ValidationFailure, sensitive.Failure?.Code);
        Assert.Equal(FailureCode.ValidationFailure, mismatched.Failure?.Code);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<IAuditReader>()
            .ReadAsync(InstallationId, 0, 10, CancellationToken.None));
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IModelRunRepository>()
            .FindByIdempotencyKeyAsync(InstallationId, "precanceled", CancellationToken.None));
    }

    [Fact]
    public async Task Started_and_completed_run_versions_and_usage_round_trip()
    {
        await SeedAsync();
        ModelRunId runId;
        await using (var scope = Services.CreateAsyncScope())
        {
            var admitted = await scope.ServiceProvider.GetRequiredService<IModelRunAdmissionService>()
                .AdmitAsync(Admission("transition round trip", "admission-transition"), CancellationToken.None);
            Assert.True(admitted.IsSuccess, admitted.Failure?.Message);
            runId = admitted.Value.Aggregate.Run.Id;
            var started = ModelRunStateMachine.Start(
                admitted.Value.Aggregate,
                "integration-worker",
                new string('I', 43),
                admitted.Value.Aggregate.Run.CreatedAt,
                admitted.Value.Aggregate.Run.CreatedAt.AddSeconds(45));
            Assert.True(started.IsSuccess, started.Failure?.Message);
            await scope.ServiceProvider.GetRequiredService<IModelRunRepository>()
                .UpdateAsync(started.Value, 0, 0, CancellationToken.None);
            Assert.True((await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        await using (var completionScope = Services.CreateAsyncScope())
        {
            var persisted = await completionScope.ServiceProvider.GetRequiredService<IModelRunRepository>()
                .FindByIdAsync(runId, CancellationToken.None);
            Assert.NotNull(persisted);
            Assert.Equal(ModelRunState.Running, persisted.Run.State);
            var completed = ModelRunStateMachine.Complete(
                persisted,
                new string('I', 43),
                new ModelUsage(120, 60, 0, 0.125m, "usd"),
                new ModelRunStreamEvidence(
                    2,
                    1,
                    "sha256:" + new string('a', 64)),
                ModelFinishReason.Stop,
                persisted.Run.StartedAt!.Value.AddSeconds(1));
            Assert.True(completed.IsSuccess, completed.Failure?.Message);
            await completionScope.ServiceProvider.GetRequiredService<IModelRunRepository>()
                .UpdateAsync(completed.Value, 1, 1, CancellationToken.None);
            Assert.True((await completionScope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        await using var verificationScope = Services.CreateAsyncScope();
        var final = await verificationScope.ServiceProvider.GetRequiredService<IModelRunRepository>()
            .FindByIdAsync(runId, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal(ModelRunState.Succeeded, final.Run.State);
        Assert.Equal(ModelRunAttemptState.Succeeded, final.Attempt.State);
        Assert.Equal(120, final.Run.Usage.InputTokens);
        Assert.Equal(0.125m, final.Run.Usage.Cost);
        Assert.Equal("USD", final.Run.Usage.Currency);
        Assert.Equal(2, final.Run.Version);
    }

    [Fact]
    public async Task Execution_starts_and_reconciles_atomically_without_persisting_model_content()
    {
        await SeedAsync();
        const string prompt = "execution prompt that must remain outside durable storage";
        const string output = "execution output that must remain outside durable storage";
        var admission = Admission(prompt, "execution-001");
        ModelRunAdmissionResult admitted;
        await using (var admissionScope = Services.CreateAsyncScope())
        {
            var result = await admissionScope.ServiceProvider.GetRequiredService<IModelRunAdmissionService>()
                .AdmitAsync(admission, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Failure?.Message);
            admitted = result.Value;
        }

        ModelRunExecutionResult executed;
        await using (var executionScope = Services.CreateAsyncScope())
        {
            var result = await executionScope.ServiceProvider.GetRequiredService<IModelRunExecutionService>()
                .ExecuteAsync(new ModelRunExecutionRequest(
                    admitted.Aggregate.Run.Id,
                    admitted.Aggregate.Run.Version,
                    admission.PlanningRequest.Request,
                    admission.ActorId,
                    "integration-worker",
                    admission.CorrelationId,
                    admission.CausationId), null, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Failure?.Message);
            executed = result.Value;
            Assert.Equal(ModelRunState.Succeeded, executed.Aggregate.Run.State);
            Assert.Equal(ModelRunAttemptState.Succeeded, executed.Aggregate.Attempt.State);
            Assert.Equal(120, executed.Aggregate.Run.Usage.InputTokens);
            Assert.Equal(60, executed.Aggregate.Run.Usage.OutputTokens);
            Assert.Equal(4, executed.Aggregate.Run.StreamEvidence.EventCount);
            Assert.NotNull(executed.Aggregate.Run.Lease);
            Assert.StartsWith(
                "sha256:",
                executed.Aggregate.Run.Lease!.TokenHash,
                StringComparison.Ordinal);
        }

        await using (var verificationScope = Services.CreateAsyncScope())
        {
            var ledger = await verificationScope.ServiceProvider
                .GetRequiredService<IModelBudgetLedgerRepository>()
                .FindAsync(AgentId, CancellationToken.None);
            Assert.NotNull(ledger);
            Assert.Equal(0, ledger.ActiveRuns);
            Assert.Equal(1, ledger.Consumption.CompletedRuns);
            Assert.Equal(120, ledger.Consumption.InputTokens);
            Assert.Equal(60, ledger.Consumption.OutputTokens);
            Assert.Equal(4, ledger.Consumption.Events);

            var healthRepository = verificationScope.ServiceProvider
                .GetRequiredService<IModelProviderHealthRepository>();
            var health = await healthRepository.FindAsync(ProviderId, CancellationToken.None);
            Assert.NotNull(health);
            Assert.Equal(ModelProviderHealthStatus.Healthy, health.Evidence.Status);
            Assert.Equal(ModelHealthEvidenceSource.Observed, health.Evidence.Source);
            Assert.Equal("attempt-succeeded", health.Evidence.EvidenceCode);
            Assert.Equal(executed.Aggregate.Run.Id, health.LastRunId);
            var durableEvidence = await ((IModelProviderHealthSource)healthRepository).ReadAsync(
                CancellationToken.None);
            Assert.True(durableEvidence.IsSuccess, durableEvidence.Failure?.Message);
            Assert.Equal(ModelProviderHealthStatus.Healthy, Assert.Single(durableEvidence.Value).Status);

            var audit = await verificationScope.ServiceProvider.GetRequiredService<IAuditReader>()
                .ReadAsync(InstallationId, 0, 10, CancellationToken.None);
            Assert.Equal(
                ["model.run-reserved", "model.run-started", "model.run-completed"],
                audit.Select(item => item.OperationType));
            Assert.True((await verificationScope.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>()
                .VerifyAsync(CancellationToken.None)).IsValid);

            var replay = await verificationScope.ServiceProvider.GetRequiredService<IModelRunExecutionService>()
                .ExecuteAsync(new ModelRunExecutionRequest(
                    admitted.Aggregate.Run.Id,
                    admitted.Aggregate.Run.Version,
                    admission.PlanningRequest.Request,
                    admission.ActorId,
                    "integration-worker",
                    admission.CorrelationId,
                    admission.CausationId), null, CancellationToken.None);
            Assert.Equal(FailureCode.ConcurrencyConflict, replay.Failure?.Code);
        }

        Assert.Equal(1, _provider?.InvocationCount);
        var databaseBytes = await File.ReadAllBytesAsync(Path.Combine(_directory, "agentforge.db"));
        Assert.Equal(-1, databaseBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(prompt)));
        Assert.Equal(-1, databaseBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(output)));
    }

    [Fact]
    public async Task Malformed_provider_stream_fails_attempt_and_releases_shared_reservation()
    {
        await SeedAsync();
        _provider!.CorruptSecondSequence = true;
        var admission = Admission("malformed stream fixture", "execution-malformed");
        ModelRunAdmissionResult admitted;
        await using (var admissionScope = Services.CreateAsyncScope())
        {
            admitted = (await admissionScope.ServiceProvider.GetRequiredService<IModelRunAdmissionService>()
                .AdmitAsync(admission, CancellationToken.None)).Value;
        }

        await using (var executionScope = Services.CreateAsyncScope())
        {
            var result = await executionScope.ServiceProvider.GetRequiredService<IModelRunExecutionService>()
                .ExecuteAsync(Execution(admitted, admission), null, CancellationToken.None);

            Assert.True(result.IsSuccess, result.Failure?.Message);
            Assert.Equal(ModelRunState.Failed, result.Value.Aggregate.Run.State);
            Assert.Equal(FailureCode.RecoverableExternalFailure, result.Value.Aggregate.Run.FailureCode);
            Assert.Equal(1, result.Value.Aggregate.Run.StreamEvidence.EventCount);
            Assert.True(result.Value.Aggregate.Attempt.IsRetryable);
        }

        await using var verificationScope = Services.CreateAsyncScope();
        var ledger = await verificationScope.ServiceProvider.GetRequiredService<IModelBudgetLedgerRepository>()
            .FindAsync(AgentId, CancellationToken.None);
        Assert.NotNull(ledger);
        Assert.Equal(0, ledger.ActiveRuns);
        Assert.Equal(1, ledger.Consumption.CompletedRuns);
        var health = await verificationScope.ServiceProvider
            .GetRequiredService<IModelProviderHealthRepository>()
            .FindAsync(ProviderId, CancellationToken.None);
        Assert.NotNull(health);
        Assert.Equal(ModelProviderHealthStatus.TemporarilyUnavailable, health.Evidence.Status);
        Assert.Equal("attempt-retryable-failure", health.Evidence.EvidenceCode);
        Assert.Equal(1, health.Evidence.ConsecutiveFailures);
        Assert.NotNull(health.Evidence.RetryAfter);
    }

    [Fact]
    public async Task Caller_cancellation_persists_canceled_terminal_evidence_and_releases_reservation()
    {
        await SeedAsync();
        var admission = Admission("cancellation fixture", "execution-canceled");
        ModelRunAdmissionResult admitted;
        await using (var admissionScope = Services.CreateAsyncScope())
        {
            admitted = (await admissionScope.ServiceProvider.GetRequiredService<IModelRunAdmissionService>()
                .AdmitAsync(admission, CancellationToken.None)).Value;
        }

        using var cancellation = new CancellationTokenSource();
        await using (var executionScope = Services.CreateAsyncScope())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                executionScope.ServiceProvider.GetRequiredService<IModelRunExecutionService>()
                    .ExecuteAsync(
                        Execution(admitted, admission),
                        new CancelingObserver(cancellation),
                        cancellation.Token));
        }

        await using var verificationScope = Services.CreateAsyncScope();
        var persisted = await verificationScope.ServiceProvider.GetRequiredService<IModelRunRepository>()
            .FindByIdAsync(admitted.Aggregate.Run.Id, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(ModelRunState.Canceled, persisted.Run.State);
        Assert.Equal(ModelRunAttemptState.Canceled, persisted.Attempt.State);
        Assert.Equal(1, persisted.Run.StreamEvidence.EventCount);
        var ledger = await verificationScope.ServiceProvider.GetRequiredService<IModelBudgetLedgerRepository>()
            .FindAsync(AgentId, CancellationToken.None);
        Assert.NotNull(ledger);
        Assert.Equal(0, ledger.ActiveRuns);
        Assert.Null(await verificationScope.ServiceProvider
            .GetRequiredService<IModelProviderHealthRepository>()
            .FindAsync(ProviderId, CancellationToken.None));
    }

    [Fact]
    public async Task Heartbeat_persists_only_the_hash_bound_monotonic_lease_evidence()
    {
        await SeedAsync();
        var admission = Admission("heartbeat fixture", "execution-heartbeat");
        ModelRunAggregate started;
        const string leaseToken = "HHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHHH";
        await using (var startScope = Services.CreateAsyncScope())
        {
            var admitted = (await startScope.ServiceProvider.GetRequiredService<IModelRunAdmissionService>()
                .AdmitAsync(admission, CancellationToken.None)).Value.Aggregate;
            started = ModelRunStateMachine.Start(
                admitted,
                "integration-worker",
                leaseToken,
                admitted.Run.CreatedAt,
                admitted.Run.CreatedAt.AddSeconds(45)).Value;
            await startScope.ServiceProvider.GetRequiredService<IModelRunRepository>()
                .UpdateAsync(started, 0, 0, CancellationToken.None);
            Assert.True((await startScope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        await using (var heartbeatScope = Services.CreateAsyncScope())
        {
            var heartbeat = await heartbeatScope.ServiceProvider.GetRequiredService<IModelRunRecoveryService>()
                .HeartbeatAsync(new ModelRunHeartbeatRequest(
                    started.Run.Id,
                    1,
                    1,
                    "integration-worker",
                    leaseToken,
                    new CorrelationId("heartbeat-test")), CancellationToken.None);

            Assert.True(heartbeat.IsSuccess, heartbeat.Failure?.Message);
            Assert.True(heartbeat.Value.Aggregate.Run.Lease!.HeartbeatAt > started.Run.Lease!.HeartbeatAt);
            Assert.Equal(started.Run.Lease.ExpiresAt, heartbeat.Value.Aggregate.Run.Lease.ExpiresAt);
            Assert.Equal(2, heartbeat.Value.Aggregate.Run.Version);
            Assert.Equal(1, heartbeat.Value.Aggregate.Attempt.Version);
        }

        var databaseBytes = await File.ReadAllBytesAsync(Path.Combine(_directory, "agentforge.db"));
        Assert.Equal(-1, databaseBytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(leaseToken)));
    }

    [Fact]
    public async Task Expired_lease_recovery_atomically_releases_budget_and_records_provider_health()
    {
        await SeedAsync();
        var admission = Admission("expired lease fixture", "execution-expired");
        ModelRunAggregate started;
        const string leaseToken = "RRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRR";
        await using (var startScope = Services.CreateAsyncScope())
        {
            var admitted = (await startScope.ServiceProvider.GetRequiredService<IModelRunAdmissionService>()
                .AdmitAsync(admission, CancellationToken.None)).Value.Aggregate;
            var reserved = ModelBudgetLedgerStateMachine.Reserve(
                null,
                admitted.Run,
                new AgentBudget(8, 4, 4_096, 1_024, 60),
                admitted.Run.CreatedAt);
            Assert.True(reserved.IsSuccess, reserved.Failure?.Message);
            started = ModelRunStateMachine.Start(
                admitted,
                "crashed-worker",
                leaseToken,
                admitted.Run.CreatedAt,
                admitted.Run.CreatedAt.AddTicks(1)).Value;
            await startScope.ServiceProvider.GetRequiredService<IModelRunRepository>()
                .UpdateAsync(started, 0, 0, CancellationToken.None);
            await startScope.ServiceProvider.GetRequiredService<IModelBudgetLedgerRepository>()
                .AddAsync(reserved.Value.Ledger, CancellationToken.None);
            Assert.True((await startScope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        ModelRunRecoveryResult recovered;
        await using (var recoveryScope = Services.CreateAsyncScope())
        {
            var result = await recoveryScope.ServiceProvider.GetRequiredService<IModelRunRecoveryService>()
                .RecoverExpiredAsync(new ModelRunRecoveryRequest(
                    started.Run.Id,
                    1,
                    1,
                    new ActorId("recovery-worker"),
                    new CorrelationId("expired-recovery")), CancellationToken.None);
            Assert.True(result.IsSuccess, result.Failure?.Message);
            recovered = result.Value;
        }

        Assert.Equal(ModelRunState.Failed, recovered.Aggregate.Run.State);
        Assert.True(recovered.Aggregate.Attempt.IsRetryable);
        Assert.Equal(started.Run.Lease!.ExpiresAt, recovered.Aggregate.Run.CompletedAt);
        Assert.Equal("lease-expired", recovered.Health.Evidence.EvidenceCode);
        Assert.Equal(ModelProviderHealthStatus.TemporarilyUnavailable, recovered.Health.Evidence.Status);

        await using (var verificationScope = Services.CreateAsyncScope())
        {
            var duplicate = await verificationScope.ServiceProvider.GetRequiredService<IModelRunRecoveryService>()
                .RecoverExpiredAsync(new ModelRunRecoveryRequest(
                    started.Run.Id,
                    1,
                    1,
                    new ActorId("recovery-worker"),
                    new CorrelationId("expired-recovery")), CancellationToken.None);
            Assert.Equal(FailureCode.ConcurrencyConflict, duplicate.Failure?.Code);
            var ledger = await verificationScope.ServiceProvider
                .GetRequiredService<IModelBudgetLedgerRepository>()
                .FindAsync(AgentId, CancellationToken.None);
            Assert.NotNull(ledger);
            Assert.Equal(0, ledger.ActiveRuns);
            Assert.Equal(1, ledger.Consumption.CompletedRuns);
            var audit = await verificationScope.ServiceProvider.GetRequiredService<IAuditReader>()
                .ReadAsync(InstallationId, 0, 10, CancellationToken.None);
            Assert.Equal(
                ["model.run-reserved", "model.run-lease-expired"],
                audit.Select(item => item.OperationType));
            Assert.True((await verificationScope.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>()
                .VerifyAsync(CancellationToken.None)).IsValid);
        }

        var databaseBytes = await File.ReadAllBytesAsync(Path.Combine(_directory, "agentforge.db"));
        Assert.Equal(-1, databaseBytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(leaseToken)));
    }

    [Fact]
    public async Task Sensitive_execution_metadata_is_rejected_before_lease_ledger_or_provider()
    {
        await SeedAsync();
        var admission = Admission("safe execution fixture", "execution-sensitive-metadata");
        ModelRunAdmissionResult admitted;
        await using (var admissionScope = Services.CreateAsyncScope())
        {
            admitted = (await admissionScope.ServiceProvider.GetRequiredService<IModelRunAdmissionService>()
                .AdmitAsync(admission, CancellationToken.None)).Value;
        }

        await using var executionScope = Services.CreateAsyncScope();
        var result = await executionScope.ServiceProvider.GetRequiredService<IModelRunExecutionService>()
            .ExecuteAsync(Execution(admitted, admission) with
            {
                WorkerId = "password=must-not-be-execution-metadata",
            }, null, CancellationToken.None);

        Assert.Equal(FailureCode.ValidationFailure, result.Failure?.Code);
        var persisted = await executionScope.ServiceProvider.GetRequiredService<IModelRunRepository>()
            .FindByIdAsync(admitted.Aggregate.Run.Id, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(ModelRunState.Reserved, persisted.Run.State);
        Assert.Null(await executionScope.ServiceProvider.GetRequiredService<IModelBudgetLedgerRepository>()
            .FindAsync(AgentId, CancellationToken.None));
        Assert.Single(await executionScope.ServiceProvider.GetRequiredService<IAuditReader>()
            .ReadAsync(InstallationId, 0, 10, CancellationToken.None));
        Assert.Equal(0, _provider?.InvocationCount);
    }

    public async Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentForge:Installation:DataDirectory"] = _directory,
                ["AgentForge:Persistence:DatabaseFileName"] = "agentforge.db",
                ["AgentForge:Persistence:EnableConnectionPooling"] = "false",
            })
            .Build();
        var services = new ServiceCollection();
        var runtimeNow = DateTimeOffset.UtcNow;
        services.AddLogging();
        services.AddAgentForgeSetup(configuration);
        services.AddAgentForgePersistence(configuration);
        services.AddAgentForgeSecurity(configuration);
        services.AddAgentForgeAudit();
        services.AddAgentForgeModels();
        var deterministic = DeterministicModelProvider.Create(
            Descriptor(runtimeNow),
            new DeterministicModelScript(
            [
                new DeterministicTextStep("execution output that must remain outside durable storage"),
                new DeterministicUsageStep(new ModelUsage(120, 60, 0, 0.125m, "usd")),
            ]),
            new RuntimeClock()).Value;
        _provider = new RecordingProvider(deterministic);
        services.AddSingleton<IModelProviderCatalog>(_ => ModelProviderCatalog.Create([
            _provider,
        ]).Value);
        services.AddSingleton<IModelProviderHealthSource>(_ => ModelProviderHealthCatalog.Create([
            new ModelProviderHealthEvidence(
                ProviderId,
                ModelProviderHealthStatus.Healthy,
                ModelHealthEvidenceSource.Probed,
                0,
                "probe-ok",
                runtimeNow.AddMinutes(-1),
                runtimeNow.AddMinutes(1)),
        ]).Value);
        _services = services.BuildServiceProvider(validateScopes: true);
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _services?.Dispose();
        if (Directory.Exists(_directory))
        {
            var fullPath = Path.GetFullPath(_directory);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            if (!fullPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullPath).StartsWith(
                    "agentforge-route-authority-",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove an unsafe route-authority fixture directory.");
            }

            Directory.Delete(fullPath, recursive: true);
        }

        return Task.CompletedTask;
    }

    private ServiceProvider Services => _services ??
        throw new InvalidOperationException("The test service provider has not been initialized.");

    private async Task SeedAsync()
    {
        await using (var installationScope = Services.CreateAsyncScope())
        {
            var installation = new InstallationSnapshot(
                InstallationId,
                InstallationState.Ready,
                7,
                Now,
                new ActorId("operator"),
                new CorrelationId("authority-installation"),
                null);
            await installationScope.ServiceProvider.GetRequiredService<IInstallationRepository>()
                .AddAsync(installation, CancellationToken.None);
            Assert.True((await installationScope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        await using var identityScope = Services.CreateAsyncScope();
        var profile = new ProviderProfile(
            ProviderId,
            InstallationId,
            "authority-provider",
            "authority-fixture",
            new Uri("https://models.example.test/v1/chat/completions"),
            "authority-model",
            new SecretReference("fixture", "provider/authority"),
            new ProviderCapabilitySummary(true, true, true, false, "probed"),
            4,
            Now.AddDays(-1),
            Now,
            new ActorId("operator"),
            new CorrelationId("authority-provider"));
        var agent = new AgentIdentity(
            AgentId,
            InstallationId,
            "authority-agent",
            null,
            null,
            "en",
            "Europe/Kiev",
            "concise",
            null,
            new AgentModelPolicy(ProviderId, ModelDataLocality.CloudAllowed, false),
            new AgentMemoryPolicy(AgentMemoryScope.Task, 30),
            new AgentCapabilityPolicy(NetworkPosture.Denied, [], []),
            new AgentBudget(8, 4, 4_096, 1_024, 60),
            new ChildAgentLimits(1, 1, 1, 1_024),
            new AgentLearningPolicy(LearningMode.Off, MutableSkillScope.None),
            3,
            Now.AddDays(-1),
            Now,
            new ActorId("operator"),
            new CorrelationId("authority-agent"));
        await identityScope.ServiceProvider.GetRequiredService<IProviderProfileRepository>()
            .AddAsync(profile, CancellationToken.None);
        await identityScope.ServiceProvider.GetRequiredService<IAgentIdentityRepository>()
            .AddAsync(agent, CancellationToken.None);
        Assert.True((await identityScope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .CommitAsync(CancellationToken.None)).Succeeded);
    }

    private static ModelProviderDescriptor Descriptor(DateTimeOffset observedAt) => new(
        ProviderId,
        "authority-fixture",
        "authority-model",
        [
            Capability(ModelCapability.TextGeneration, observedAt),
            Capability(ModelCapability.Streaming, observedAt),
        ],
        new ModelProviderRoutingEvidence(
            ModelProviderDataLocation.Cloud,
            ModelCapabilityEvidenceSource.PolicyApproved,
            8_192,
            1_024,
            9_500,
            1,
            2,
            200,
            observedAt.AddMinutes(-5),
            observedAt.AddMinutes(5)));

    private static ModelCapabilityEvidence Capability(
        ModelCapability capability,
        DateTimeOffset observedAt) => new(
        capability,
        ModelCapabilityEvidenceSource.Probed,
        ModelCapabilityAvailability.Available,
        "Current integration evidence.",
        observedAt.AddMinutes(-5),
        observedAt.AddMinutes(5));

    private static ModelRunAdmissionRequest Admission(string prompt, string idempotencyKey)
    {
        var correlation = new CorrelationId("model-run-admission");
        var request = new ModelRequest(
            new ModelRequestId(Guid.Parse("7321b61a-4b71-45b2-b99c-b09ca7f9b19e")),
            "authority-model",
            [new ModelMessage(ModelMessageRole.User, [new ModelTextContent(prompt)])],
            [],
            new ModelResponseFormat(ModelResponseFormatKind.Text),
            new ModelInvocationLimits(100, 0, 32, 30),
            0,
            1,
            42,
            correlation);
        return new ModelRunAdmissionRequest(
            new ModelRoutePlanningRequest(InstallationId, 7, AgentId, 3, request, 200, []),
            new ActorId("model-worker"),
            idempotencyKey,
            correlation);
    }

    private async Task<DomainResult<ModelRunAdmissionResult>> AdmitInNewScopeAsync(
        ModelRunAdmissionRequest request)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IModelRunAdmissionService>()
            .AdmitAsync(request, CancellationToken.None);
    }

    private static ModelRunExecutionRequest Execution(
        ModelRunAdmissionResult admitted,
        ModelRunAdmissionRequest admission) => new(
        admitted.Aggregate.Run.Id,
        admitted.Aggregate.Run.Version,
        admission.PlanningRequest.Request,
        admission.ActorId,
        "integration-worker",
        admission.CorrelationId,
        admission.CausationId);

    private sealed class RecordingProvider(IModelProvider inner) : IModelProvider
    {
        private int _invocationCount;

        public ModelProviderDescriptor Descriptor => inner.Descriptor;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public bool CorruptSecondSequence { get; set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocationCount);
            var index = 0;
            await foreach (var modelEvent in inner.StreamAsync(request, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                yield return CorruptSecondSequence && index++ == 1
                    ? modelEvent with { Sequence = 99 }
                    : modelEvent;
            }
        }
    }

    private sealed class CancelingObserver(CancellationTokenSource cancellation) : IModelRunEventObserver
    {
        public ValueTask ObserveAsync(ModelStreamEvent modelEvent, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RuntimeClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

}
