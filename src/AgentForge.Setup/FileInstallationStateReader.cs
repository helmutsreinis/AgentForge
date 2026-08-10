using System.Text.Json;
using AgentForge.Abstractions.Installations;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentForge.Setup;

public sealed partial class FileInstallationStateReader(
    IOptions<InstallationOptions> options,
    ILogger<FileInstallationStateReader> logger) : IInstallationStateReader
{
    private static readonly ActorId BootstrapActor = new("bootstrap-kernel");
    private static readonly CorrelationId BootstrapCorrelation = new("bootstrap");

    public async ValueTask<InstallationSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var dataDirectory = InstallationPathResolver.ResolveConfiguredDataDirectory(options.Value);
        var statePath = Path.Combine(dataDirectory, options.Value.StateFileName);

        if (!File.Exists(statePath))
        {
            return InstallationSnapshot.CreateUninitialized(
                new InstallationId(Guid.Empty),
                DateTimeOffset.UnixEpoch,
                BootstrapActor,
                BootstrapCorrelation);
        }

        try
        {
            await using var stream = new FileStream(
                statePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var document = await JsonSerializer.DeserializeAsync<InstallationStateDocument>(
                stream,
                cancellationToken: cancellationToken);

            if (document is null || document.InstallationId is null || document.InstallationId == Guid.Empty)
            {
                return Recovery("The installation state file is incomplete.");
            }

            return new InstallationSnapshot(
                new InstallationId(document.InstallationId.Value),
                document.State,
                document.Version,
                document.UpdatedAt,
                new ActorId(document.ActorId ?? "unknown"),
                new CorrelationId(document.CorrelationId ?? "unknown"),
                document.RecoveryReason);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            LogStateReadFailure(logger, exception);
            return Recovery("The installation state file cannot be read safely.");
        }
    }

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Error,
        Message = "Installation state could not be read; entering recovery mode")]
    private static partial void LogStateReadFailure(ILogger logger, Exception exception);

    private static InstallationSnapshot Recovery(string reason) => new(
        new InstallationId(Guid.Empty),
        InstallationState.RecoveryRequired,
        0,
        DateTimeOffset.UnixEpoch,
        BootstrapActor,
        BootstrapCorrelation,
        reason);

    private sealed record InstallationStateDocument(
        Guid? InstallationId,
        InstallationState State,
        long Version,
        DateTimeOffset UpdatedAt,
        string? ActorId,
        string? CorrelationId,
        string? RecoveryReason);
}
