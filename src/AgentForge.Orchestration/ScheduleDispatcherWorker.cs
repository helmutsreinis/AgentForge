using AgentForge.Abstractions.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentForge.Orchestration;

internal sealed class ScheduleDispatcherWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ScheduleDispatcherWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> ScanFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(4100, "ScheduleScanFailed"),
        "The bounded schedule scan failed.");

    private static readonly Action<ILogger, string, string, Exception?> DueTransitionSkipped = LoggerMessage.Define<string, string>(
        LogLevel.Warning,
        new EventId(4101, "ScheduleDueTransitionSkipped"),
        "Schedule {ScheduleId} due transition was skipped with {FailureCode}.");

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
        var schedules = await store.ListDueAsync(timeProvider.GetUtcNow(), 64, cancellationToken);
        foreach (var schedule in schedules)
        {
            await using var itemScope = scopeFactory.CreateAsyncScope();
            var service = itemScope.ServiceProvider.GetRequiredService<IScheduleService>();
            var result = await service.EvaluateDueAsync(
                schedule.ScheduleId,
                schedule.Version,
                cancellationToken);
            if (!result.IsSuccess)
            {
                DueTransitionSkipped(
                    logger,
                    schedule.ScheduleId.ToString(),
                    result.Failure?.Code.ToString() ?? "Unknown",
                    null);
            }
        }
    }
}
