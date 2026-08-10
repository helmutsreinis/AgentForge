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
using AgentForge.Domain.Persistence;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Security;
using AgentForge.Domain.Setup;
using AgentForge.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.UnitTests;

public sealed class SetupApplicationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Returns_typed_retryable_conflict_when_atomic_commit_loses_a_race()
    {
        var unitOfWork = new StubUnitOfWork(CommitResult.ConcurrencyConflict("stale"));
        await using var services = BuildServices(unitOfWork);
        await using var scope = services.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISetupApplicationService>()
            .BeginAsync(new BeginSetupRequest(
                new InstallationId(Guid.Parse("7683d21d-b311-477f-baf2-73776e8bc857")),
                new ActorId("operator"),
                new CorrelationId("setup-conflict")), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.ConcurrencyConflict, result.Failure?.Code);
        Assert.True(result.Failure?.IsRetryable);
        Assert.Equal(1, unitOfWork.CommitCount);
    }

    [Fact]
    public async Task Rejects_control_characters_before_reading_or_writing_state()
    {
        var repository = new StubInstallationRepository();
        await using var services = BuildServices(new StubUnitOfWork(CommitResult.Success(2)), repository);
        await using var scope = services.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISetupApplicationService>()
            .BeginAsync(new BeginSetupRequest(
                null,
                new ActorId("operator\nforged"),
                new CorrelationId("setup-invalid")), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.ValidationFailure, result.Failure?.Code);
        Assert.Equal(0, repository.ReadCount);
    }

    [Fact]
    public async Task Rejects_provider_endpoint_credentials_before_reading_state()
    {
        var repository = new StubInstallationRepository();
        await using var services = BuildServices(new StubUnitOfWork(CommitResult.Success(2)), repository);
        await using var scope = services.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISetupApplicationService>()
            .ConfigureProviderAsync(new ConfigureProviderRequest(
                new ProviderProfileCandidate(
                    "primary",
                    "deterministic",
                    new Uri("https://user:" + "password@example.test/v1"),
                    "model",
                    new SecretReference("store", "key")),
                new ActorId("operator"),
                new CorrelationId("provider-invalid")), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.ValidationFailure, result.Failure?.Code);
        Assert.Equal(0, repository.ReadCount);
    }

    [Fact]
    public async Task Provider_credential_is_compensated_when_atomic_profile_commit_fails()
    {
        var installation = InstallationSnapshot.CreateUninitialized(
            new InstallationId(Guid.Parse("9123db1f-526b-4f21-8176-851337e1b566")),
            Now,
            new ActorId("operator"),
            new CorrelationId("provider-begin")) with
        {
            State = InstallationState.Configuring,
            Version = 1,
        };
        var secretStore = new StubSecretStore();
        await using var services = BuildServices(
            new StubUnitOfWork(CommitResult.ConcurrencyConflict("provider race")),
            new StubInstallationRepository(installation),
            secretStore);
        await using var scope = services.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISetupApplicationService>()
            .ConfigureProviderCredentialAsync(new ConfigureProviderCredentialRequest(
                "primary",
                "deterministic",
                new Uri("http://127.0.0.1:9000/v1"),
                "model",
                "temporary-secret".AsMemory(),
                new ActorId("operator"),
                new CorrelationId("provider-race")), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.ConcurrencyConflict, result.Failure?.Code);
        Assert.Equal(1, secretStore.StoreCount);
        Assert.Equal(1, secretStore.DeleteCount);
    }

    private static ServiceProvider BuildServices(
        IUnitOfWork unitOfWork,
        StubInstallationRepository? repository = null,
        ISecretStore? secretStore = null)
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddAgentForgeSetup(configuration);
        services.AddSingleton<IInstallationRepository>(repository ?? new StubInstallationRepository());
        services.AddSingleton<IProviderProfileRepository, StubProviderProfileRepository>();
        services.AddSingleton<IAgentIdentityRepository, StubAgentIdentityRepository>();
        services.AddSingleton<ILocalAdministratorRepository, StubAdministratorRepository>();
        services.AddSingleton<ILocalAdministratorCredentialService, StubAdministratorCredentialService>();
        services.AddSingleton<ILocalAdministratorAuthenticator, StubAdministratorAuthenticator>();
        services.AddSingleton(secretStore ?? new StubSecretStore());
        services.AddSingleton<ISensitiveDataRedactor, StubSensitiveDataRedactor>();
        services.AddSingleton<IProviderProfileValidator, StubProviderValidator>();
        services.AddSingleton<IAuditRecorder, StubAuditRecorder>();
        services.AddSingleton<IAuditIntegrityVerifier, StubAuditIntegrityVerifier>();
        services.AddSingleton(unitOfWork);
        services.AddSingleton<IClock, StubTime>();
        services.AddSingleton<IIdentifierGenerator, StubTime>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private sealed class StubInstallationRepository(InstallationSnapshot? snapshot = null) : IInstallationRepository
    {
        public int ReadCount { get; private set; }

        public ValueTask<InstallationSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTask.FromResult(snapshot ?? InstallationSnapshot.CreateUninitialized(
                    new InstallationId(Guid.Empty),
                    DateTimeOffset.UnixEpoch,
                    new ActorId("bootstrap"),
                    new CorrelationId("bootstrap")));
        }

        public ValueTask AddAsync(InstallationSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask UpdateAsync(
            InstallationSnapshot snapshot,
            long expectedVersion,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubAuditRecorder : IAuditRecorder
    {
        public Task<AuditRecordResult> RecordAsync(
            AuditRecordRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var auditEvent = new AuditEventRecord(
                Guid.Parse("0840a169-97b5-4748-ac86-4684221a9ddc"),
                1,
                Now,
                request.InstallationId,
                request.ActorId,
                request.CorrelationId,
                request.CausationId,
                request.OperationType,
                request.Outcome,
                RedactedData.Empty,
                RedactedData.Empty,
                request.ErrorClassification,
                AuditEventHasher.GenesisHash,
                new string('1', 64));
            return Task.FromResult(new AuditRecordResult(auditEvent, 0, 0));
        }
    }

    private sealed class StubProviderProfileRepository : IProviderProfileRepository
    {
        public ValueTask AddAsync(ProviderProfile profile, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask UpdateAsync(
            ProviderProfile profile,
            long expectedVersion,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ProviderProfile?> FindByIdAsync(
            ProviderProfileId profileId,
            CancellationToken cancellationToken) => ValueTask.FromResult<ProviderProfile?>(null);

        public ValueTask<ProviderProfile?> FindByNameAsync(
            InstallationId installationId,
            string name,
            CancellationToken cancellationToken) => ValueTask.FromResult<ProviderProfile?>(null);

        public Task<IReadOnlyList<ProviderProfile>> ListAsync(
            InstallationId installationId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProviderProfile>>([]);
    }

    private sealed class StubAgentIdentityRepository : IAgentIdentityRepository
    {
        public ValueTask AddAsync(AgentIdentity agent, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask UpdateAsync(
            AgentIdentity agent,
            long expectedVersion,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<AgentIdentity?> FindByNameAsync(
            InstallationId installationId,
            string name,
            CancellationToken cancellationToken) => ValueTask.FromResult<AgentIdentity?>(null);

        public ValueTask<AgentIdentity?> FindByIdAsync(
            AgentIdentityId agentId,
            CancellationToken cancellationToken) => ValueTask.FromResult<AgentIdentity?>(null);

        public Task<IReadOnlyList<AgentIdentity>> ListAsync(
            InstallationId installationId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AgentIdentity>>([]);
    }

    private sealed class StubAdministratorRepository : ILocalAdministratorRepository
    {
        public ValueTask AddAsync(LocalAdministrator administrator, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<LocalAdministrator?> FindAsync(
            InstallationId installationId,
            CancellationToken cancellationToken) => ValueTask.FromResult<LocalAdministrator?>(null);
    }

    private sealed class StubAdministratorCredentialService : ILocalAdministratorCredentialService
    {
        public Task<DomainResult<GeneratedAdministratorCredential>> CreateAsync(
            string logicalName,
            CancellationToken cancellationToken) => Task.FromResult(DomainResult.Fail<GeneratedAdministratorCredential>(
                new DomainFailure(FailureCode.UnsupportedCapability, "stub")));

        public bool Verify(ReadOnlySpan<char> credential, AdministratorCredentialVerifier verifier) => false;
    }

    private sealed class StubAdministratorAuthenticator : ILocalAdministratorAuthenticator
    {
        public Task<DomainResult<ActorId>> AuthenticateAsync(
            InstallationId installationId,
            ReadOnlyMemory<char> credential,
            CancellationToken cancellationToken) => Task.FromResult(DomainResult.Fail<ActorId>(new DomainFailure(
                FailureCode.PolicyDenied,
                "stub")));
    }

    private sealed class StubSecretStore : ISecretStore
    {
        public string StoreName => "stub";

        public int StoreCount { get; private set; }

        public int DeleteCount { get; private set; }

        public SecretStoreCapability GetCapability() => new(StoreName, false, new DomainFailure(
            FailureCode.UnsupportedCapability,
            "stub"));

        public Task<DomainResult<SecretReference>> StoreAsync(
            string logicalName,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StoreCount++;
            return Task.FromResult(DomainResult.Success(new SecretReference(StoreName, "stored-reference")));
        }

        public Task<DomainResult<SecretLease>> MaterializeAsync(
            SecretReference secretReference,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DomainResult<bool>> DeleteAsync(
            SecretReference secretReference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCount++;
            return Task.FromResult(DomainResult.Success(true));
        }
    }

    private sealed class StubSensitiveDataRedactor : ISensitiveDataRedactor
    {
        public RedactionResult Redact(object? value) => new(RedactedData.Empty, 0);
    }

    private sealed class StubAuditIntegrityVerifier : IAuditIntegrityVerifier
    {
        public Task<AuditVerificationResult> VerifyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AuditVerificationResult(true, 0, null, null, AuditEventHasher.GenesisHash));
    }

    private sealed class StubProviderValidator : IProviderProfileValidator
    {
        public Task<DomainResult<ProviderCapabilitySummary>> ValidateAsync(
            ProviderProfileCandidate candidate,
            CancellationToken cancellationToken) => Task.FromResult(DomainResult.Success(new ProviderCapabilitySummary(
                true,
                true,
                true,
                false,
                "stub")));
    }

    private sealed class StubUnitOfWork(CommitResult result) : IUnitOfWork
    {
        public int CommitCount { get; private set; }

        public Task<CommitResult> CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommitCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class StubTime : IClock, IIdentifierGenerator
    {
        public DateTimeOffset UtcNow => Now;

        public Guid NewGuid() => Guid.Parse("e89f2d77-96a3-4d5c-a3ca-d934ee083b97");
    }
}
