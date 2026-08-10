using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Setup;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Persistence;
using AgentForge.Domain.Primitives;
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

    private static ServiceProvider BuildServices(
        IUnitOfWork unitOfWork,
        StubInstallationRepository? repository = null)
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddAgentForgeSetup(configuration);
        services.AddSingleton<IInstallationRepository>(repository ?? new StubInstallationRepository());
        services.AddSingleton<IAuditRecorder, StubAuditRecorder>();
        services.AddSingleton(unitOfWork);
        services.AddSingleton<IClock, StubTime>();
        services.AddSingleton<IIdentifierGenerator, StubTime>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private sealed class StubInstallationRepository : IInstallationRepository
    {
        public int ReadCount { get; private set; }

        public ValueTask<InstallationSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTask.FromResult(InstallationSnapshot.CreateUninitialized(
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
