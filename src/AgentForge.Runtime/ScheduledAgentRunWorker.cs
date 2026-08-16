using System.Security.Cryptography;
using System.Text;
using AgentForge.Abstractions.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentForge.Runtime;

internal sealed class ScheduledAgentRunWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ScheduledAgentRunWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> ScanFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(4300, "ScheduledAgentRunScanFailed"),
        "The bounded scheduled-agent-run scan failed.");

    private static readonly Action<ILogger, string, string, Exception?> ExecutionFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(4301, "ScheduledAgentRunFailed"),
            "Scheduled occurrence {OccurrenceIdHash} failed with {FailureCode}.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                ScanFailed(logger, exception);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduleSnapshotStore>();
        var runnable = await store.ListRunnableAsync(timeProvider.GetUtcNow(), 16, cancellationToken);
        foreach (var item in runnable)
        {
            await using var itemScope = scopeFactory.CreateAsyncScope();
            var templates = itemScope.ServiceProvider.GetRequiredService<IScheduledAgentRunStore>();
            if (await templates.FindAsync(item.ScheduleId, cancellationToken) is null)
            {
                continue;
            }
            var schedules = itemScope.ServiceProvider.GetRequiredService<IScheduleService>();
            if (item.RequiresLeaseRecovery)
            {
                _ = await schedules.RecoverExpiredAsync(item.ScheduleId, item.Version, cancellationToken);
                continue;
            }

            var claimed = await schedules.ClaimAsync(
                item.ScheduleId,
                item.Version,
                item.OccurrenceIdHash,
                "scheduled-agent-run-worker",
                TimeSpan.FromMinutes(5),
                cancellationToken);
            if (!claimed.IsSuccess)
            {
                continue;
            }

            var executor = itemScope.ServiceProvider.GetRequiredService<IScheduledAgentRunService>();
            var execution = await executor.ExecuteAsync(
                claimed.Value.Snapshot,
                item.OccurrenceIdHash,
                cancellationToken);
            if (execution.IsSuccess)
            {
                _ = await schedules.CompleteAsync(
                    item.ScheduleId,
                    claimed.Value.Snapshot.Version,
                    item.OccurrenceIdHash,
                    "scheduled-agent-run-worker",
                    claimed.Value.LeaseToken,
                    execution.Value.EvidenceHash,
                    CancellationToken.None);
                continue;
            }

            var evidence = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{item.OccurrenceIdHash}\n{execution.Failure!.Code}\n{execution.Failure.IsRetryable}")))}";
            _ = await schedules.FailAsync(
                item.ScheduleId,
                claimed.Value.Snapshot.Version,
                item.OccurrenceIdHash,
                "scheduled-agent-run-worker",
                claimed.Value.LeaseToken,
                evidence,
                CancellationToken.None);
            ExecutionFailed(logger, item.OccurrenceIdHash, execution.Failure.Code.ToString(), null);
        }
    }
}
