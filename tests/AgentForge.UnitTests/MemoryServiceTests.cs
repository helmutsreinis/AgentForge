using System.Collections.Immutable;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Memory;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Memory;
using AgentForge.Domain.Persistence;
using AgentForge.Domain.Primitives;
using AgentForge.Memory;
using AgentForge.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.UnitTests;

public sealed class MemoryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 13, 0, 0, TimeSpan.Zero);
    private static readonly InstallationId InstallationId = new(Guid.Parse("b9e05e33-d2dd-4161-bf8b-1c19d55cdd15"));
    private static readonly AgentIdentityId AgentId = new(Guid.Parse("ab2ecf6c-7b24-487d-beee-06d35a8d4ca8"));

    [Theory]
    [InlineData(MemoryKind.Working, MemorySourceKind.UserInput)]
    [InlineData(MemoryKind.Task, MemorySourceKind.TaskEvidence)]
    [InlineData(MemoryKind.Episodic, MemorySourceKind.Trajectory)]
    [InlineData(MemoryKind.Semantic, MemorySourceKind.SearchCitation)]
    [InlineData(MemoryKind.User, MemorySourceKind.UserCorrection)]
    [InlineData(MemoryKind.Environment, MemorySourceKind.EnvironmentProfile)]
    [InlineData(MemoryKind.Procedural, MemorySourceKind.SkillReceipt)]
    public async Task Creates_each_separate_memory_kind_with_exact_source(
        MemoryKind kind,
        MemorySourceKind sourceKind)
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IMemoryService>();

        var result = await service.CreateAsync(Request(kind, sourceKind), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(kind, result.Value.Kind);
        Assert.Equal(sourceKind, result.Value.Source.Kind);
        Assert.Equal(0, result.Value.Version);
    }

    [Fact]
    public async Task Redacts_before_persistence_and_replays_only_exact_idempotency()
    {
        var repository = new FakeMemoryRepository();
        await using var provider = BuildProvider(repository);
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var request = Request(MemoryKind.User, MemorySourceKind.UserCorrection) with
        {
            Content = "password=do-not-store-this",
        };

        var first = await service.CreateAsync(request, CancellationToken.None);
        var replay = await service.CreateAsync(request, CancellationToken.None);
        var conflict = await service.CreateAsync(request with { Content = "changed safe value" }, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.Equal("[REDACTED]", first.Value.Content);
        Assert.Equal(1, first.Value.RedactionCount);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(FailureCode.ConcurrencyConflict, conflict.Failure?.Code);
        Assert.DoesNotContain("do-not-store-this", repository.Entries.Single().Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retrieval_is_scope_kind_retention_and_text_bounded_then_deletion_is_exact()
    {
        var repository = new FakeMemoryRepository();
        var audit = new FakeAuditRecorder();
        await using var provider = BuildProvider(repository, audit);
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var created = await service.CreateAsync(Request(MemoryKind.Semantic, MemorySourceKind.SearchCitation) with
        {
            Content = "SQLite uses a write-ahead log",
        }, CancellationToken.None);
        Assert.True(created.IsSuccess);

        var found = await service.SearchAsync(new MemoryQuery(
            InstallationId, AgentId, "task:alpha", "write-ahead", [MemoryKind.Semantic], 10, Now), CancellationToken.None);
        var wrongScope = await service.SearchAsync(new MemoryQuery(
            InstallationId, AgentId, "task:other", "write-ahead", [MemoryKind.Semantic], 10, Now), CancellationToken.None);
        var denied = await service.DeleteAsync(new DeleteMemoryRequest(
            created.Value.Id, InstallationId, AgentId, "task:other", new ActorId("operator"),
            new CorrelationId("delete-denied"), null), CancellationToken.None);
        var deleted = await service.DeleteAsync(new DeleteMemoryRequest(
            created.Value.Id, InstallationId, AgentId, "task:alpha", new ActorId("operator"),
            new CorrelationId("delete-ok"), null), CancellationToken.None);

        Assert.Single(found.Value);
        Assert.Empty(wrongScope.Value);
        Assert.Equal(FailureCode.PolicyDenied, denied.Failure?.Code);
        Assert.True(deleted.Value);
        Assert.Empty(repository.Entries);
        Assert.Contains("memory.deleted", audit.Operations);
    }

    [Fact]
    public async Task Invalid_source_and_excess_working_retention_fail_before_storage()
    {
        var repository = new FakeMemoryRepository();
        await using var provider = BuildProvider(repository);
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IMemoryService>();

        var sourceMismatch = await service.CreateAsync(
            Request(MemoryKind.Semantic, MemorySourceKind.UserInput), CancellationToken.None);
        var retention = await service.CreateAsync(Request(MemoryKind.Working, MemorySourceKind.UserInput) with
        {
            ExpiresAtUtc = Now.AddDays(2),
            Id = new MemoryEntryId(Guid.NewGuid()),
            IdempotencyKey = "memory-retention",
        }, CancellationToken.None);

        Assert.Equal(FailureCode.ValidationFailure, sourceMismatch.Failure?.Code);
        Assert.Equal(FailureCode.ValidationFailure, retention.Failure?.Code);
        Assert.Empty(repository.Entries);
    }

    private static ServiceProvider BuildProvider(
        FakeMemoryRepository? repository = null,
        FakeAuditRecorder? audit = null)
    {
        var services = new ServiceCollection();
        services.AddAgentForgeSecurity(new ConfigurationBuilder().Build());
        services.AddAgentForgeMemory();
        services.AddSingleton<IMemoryRepository>(repository ?? new FakeMemoryRepository());
        services.AddSingleton<IAuditRecorder>(audit ?? new FakeAuditRecorder());
        services.AddSingleton<IUnitOfWork, SuccessfulUnitOfWork>();
        services.AddSingleton<IClock, FixedClock>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static CreateMemoryRequest Request(MemoryKind kind, MemorySourceKind sourceKind) => new(
        new MemoryEntryId(Guid.NewGuid()),
        InstallationId,
        AgentId,
        "task:alpha",
        kind,
        $"content for {kind}",
        new MemorySource(sourceKind, "source-1", $"sha256:{new string('a', 64)}", new Uri("https://example.test/source")),
        Now.AddHours(12),
        new ActorId("operator"),
        new CorrelationId("memory-create"),
        null,
        $"memory-{kind.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}");

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class SuccessfulUnitOfWork : IUnitOfWork
    {
        public Task<CommitResult> CommitAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CommitResult.Success(1));
    }

    private sealed class FakeAuditRecorder : IAuditRecorder
    {
        public List<string> Operations { get; } = [];

        public Task<AuditRecordResult> RecordAsync(AuditRecordRequest request, CancellationToken cancellationToken)
        {
            Operations.Add(request.OperationType);
            var record = new AuditEventRecord(
                Guid.NewGuid(), Operations.Count, Now, request.InstallationId, request.ActorId,
                request.CorrelationId, request.CausationId, request.OperationType, request.Outcome,
                RedactedData.Empty, RedactedData.Empty, null, new string('0', 64), new string('1', 64));
            return Task.FromResult(new AuditRecordResult(record, 0, 0));
        }
    }

    private sealed class FakeMemoryRepository : IMemoryRepository
    {
        public List<MemoryEntry> Entries { get; } = [];

        public ValueTask<MemoryEntry?> FindByIdAsync(MemoryEntryId id, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Entries.SingleOrDefault(item => item.Id == id));

        public ValueTask<MemoryEntry?> FindByIdempotencyKeyAsync(
            InstallationId installationId,
            string idempotencyKey,
            CancellationToken cancellationToken) => ValueTask.FromResult(Entries.SingleOrDefault(
                item => item.InstallationId == installationId && item.IdempotencyKey == idempotencyKey));

        public ValueTask AddAsync(MemoryEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<MemoryEntry>>(Entries.Where(item =>
                item.InstallationId == query.InstallationId && item.AgentId == query.AgentId &&
                item.ScopeId == query.ScopeId && item.ExpiresAtUtc > query.AsOfUtc && query.Kinds.Contains(item.Kind) &&
                item.Content.Contains(query.Text, StringComparison.OrdinalIgnoreCase)).Take(query.MaximumResults).ToArray());

        public ValueTask DeleteAsync(MemoryEntryId id, CancellationToken cancellationToken)
        {
            Entries.RemoveAll(item => item.Id == id);
            return ValueTask.CompletedTask;
        }
    }
}
