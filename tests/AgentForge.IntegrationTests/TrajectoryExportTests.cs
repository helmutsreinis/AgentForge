using System.Text.Json;
using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Audit;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using AgentForge.Persistence;
using AgentForge.Security;
using AgentForge.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.IntegrationTests;

public sealed class TrajectoryExportTests : IDisposable
{
    private const string RawSecret = "sk-this-must-never-appear-1234567890";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"agentforge-trajectory-{Guid.NewGuid():N}");

    [Fact]
    public async Task Complete_trajectory_is_redacted_reconstructable_integrity_bound_and_idempotent()
    {
        var installationId = new InstallationId(Guid.NewGuid());
        var actor = new ActorId("trajectory-operator");
        var correlation = new CorrelationId("trajectory-correlation");
        var configuration = Configuration();
        TrajectoryExportReceipt receipt;
        await using (var provider = Build(configuration))
        {
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
                .InitializeAsync(CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IInstallationRepository>().AddAsync(
                InstallationSnapshot.CreateUninitialized(
                    installationId, DateTimeOffset.UtcNow, actor, correlation), CancellationToken.None);
            var recorder = scope.ServiceProvider.GetRequiredService<IAuditRecorder>();
            await RecordAsync(recorder, installationId, actor, correlation,
                "task.intake", AuditOutcome.Succeeded, new { objectiveHash = Hash('a') }, new { taskId = "task-1" });
            await RecordAsync(recorder, installationId, actor, correlation,
                "context.prepare", AuditOutcome.Succeeded,
                new { apiKey = RawSecret, sourceHashes = new[] { Hash('b') } }, new { contextHash = Hash('c') });
            await RecordAsync(recorder, installationId, actor, correlation,
                "run.snapshot.persist", AuditOutcome.Succeeded, new { phase = "Plan" }, new { snapshotHash = Hash('d') });
            await RecordAsync(recorder, installationId, actor, correlation,
                "model.call.complete", AuditOutcome.Succeeded, new { provider = "deterministic" },
                new { usage = new { inputTokens = 10, outputTokens = 5 } });
            await RecordAsync(recorder, installationId, actor, correlation,
                "tool.invoke.complete", AuditOutcome.Succeeded, new { tool = "test:bounded" },
                new { outputHash = Hash('e'), outputLength = 42 });
            await RecordAsync(recorder, installationId, actor, correlation,
                "model.retry", AuditOutcome.Failed, new { attempt = 1 }, new { retry = true }, "TransientProvider");
            await RecordAsync(recorder, installationId, actor, correlation,
                "coding.verification.complete", AuditOutcome.Succeeded, new { verifier = "test" },
                new { passed = true, evidenceHash = Hash('f') });
            await RecordAsync(recorder, installationId, actor, correlation,
                "task.transition.complete", AuditOutcome.Succeeded, new { from = "Running" }, new { to = "Completed" });
            var commit = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None);
            Assert.True(commit.Succeeded, commit.Failure?.Message);

            var exporter = scope.ServiceProvider.GetRequiredService<ITrajectoryExporter>();
            var request = new TrajectoryExportRequest(
                installationId, 0, 100, correlation, actor, "trajectory-export-1",
                new CorrelationId("export-correlation"));
            var exported = await exporter.ExportAsync(request, CancellationToken.None);
            Assert.True(exported.IsSuccess, exported.Failure?.Message);
            receipt = exported.Value;
            Assert.Equal(8, receipt.EventCount);
            Assert.Equal(receipt.ExportHash, receipt.Artifact.ContentHash);

            await using var content = await scope.ServiceProvider.GetRequiredService<IArtifactStore>()
                .OpenReadAsync(receipt.Artifact, CancellationToken.None);
            using var reader = new StreamReader(content);
            var json = await reader.ReadToEndAsync(CancellationToken.None);
            Assert.DoesNotContain(RawSecret, json, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", json, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(json);
            var events = document.RootElement.GetProperty("events").EnumerateArray().ToArray();
            Assert.Equal(8, events.Length);
            Assert.Equal(Enumerable.Range(1, 8).Select(value => (long)value),
                events.Select(item => item.GetProperty("sequence").GetInt64()));
            var stages = events.Select(item => item.GetProperty("stage").GetString()).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("Intake", stages);
            Assert.Contains("Context", stages);
            Assert.Contains("Snapshot", stages);
            Assert.Contains("ModelCall", stages);
            Assert.Contains("ToolCall", stages);
            Assert.Contains("Retry", stages);
            Assert.Contains("Verification", stages);
            Assert.Contains("StateTransition", stages);
            Assert.Equal("TransientProvider", events[5].GetProperty("errorClassification").GetString());
            Assert.Equal(1, events.Sum(item => item.GetProperty("secondaryRedactionCount").GetInt32()));

            var replay = await exporter.ExportAsync(request, CancellationToken.None);
            Assert.True(replay.IsSuccess, replay.Failure?.Message);
            Assert.Equal(receipt.ExportId, replay.Value.ExportId);
            var conflict = await exporter.ExportAsync(request with { MaximumEvents = 7 }, CancellationToken.None);
            Assert.False(conflict.IsSuccess);
            Assert.Equal(FailureCode.ConcurrencyConflict, conflict.Failure!.Code);
        }

        await using (var restarted = Build(configuration))
        {
            await using var scope = restarted.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
                .InitializeAsync(CancellationToken.None);
            var replay = await scope.ServiceProvider.GetRequiredService<ITrajectoryExporter>().ExportAsync(
                new TrajectoryExportRequest(
                    installationId, 0, 100, correlation, actor, "trajectory-export-1",
                    new CorrelationId("export-correlation")), CancellationToken.None);
            Assert.True(replay.IsSuccess, replay.Failure?.Message);
            Assert.Equal(receipt.ExportId, replay.Value.ExportId);
            Assert.True((await scope.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>()
                .VerifyAsync(CancellationToken.None)).IsValid);
        }
        var database = await File.ReadAllBytesAsync(Path.Combine(_directory, "agentforge.db"));
        Assert.False(database.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(RawSecret)) >= 0);
    }

    private static Task<AuditRecordResult> RecordAsync(
        IAuditRecorder recorder,
        InstallationId installationId,
        ActorId actor,
        CorrelationId correlation,
        string operation,
        AuditOutcome outcome,
        object input,
        object output,
        string? error = null) => recorder.RecordAsync(new AuditRecordRequest(
            installationId, actor, correlation, null, operation, outcome, input, output, error),
            CancellationToken.None);

    private static ServiceProvider Build(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentForgeSetup(configuration);
        services.AddAgentForgePersistence(configuration);
        services.AddAgentForgeSecurity(configuration);
        services.AddAgentForgeAudit();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["AgentForge:Installation:DataDirectory"] = _directory,
            ["AgentForge:Persistence:EnableConnectionPooling"] = "false",
        }).Build();

    private static string Hash(char value) => $"sha256:{new string(value, 64)}";

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
