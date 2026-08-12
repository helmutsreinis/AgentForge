using System.Text;
using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Environments;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Security;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Environments;
using AgentForge.Domain.Primitives;

namespace AgentForge.Environment;

internal sealed class EnvironmentInventoryService(
    IEnvironmentProfiler profiler,
    IInstallationRepository installations,
    IArtifactStore artifacts,
    ISensitiveDataRedactor redactor,
    IAuditRecorder auditRecorder,
    IUnitOfWork unitOfWork) : IEnvironmentInventoryService
{
    public async Task<DomainResult<EnvironmentInventoryResult>> CaptureAsync(
        CaptureEnvironmentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var captured = await profiler.CaptureAsync(request, cancellationToken);
        if (!captured.IsSuccess)
        {
            return DomainResult.Fail<EnvironmentInventoryResult>(captured.Failure!);
        }

        try
        {
            var redacted = redactor.Redact(new
            {
                DocumentType = "agentforge.environment-profile",
                SchemaVersion = captured.Value.SchemaVersion,
                Profile = captured.Value,
            });
            var bytes = Encoding.UTF8.GetBytes(redacted.Data.Json);
            await using var content = new MemoryStream(bytes, writable: false);
            var artifact = await artifacts.PutAsync(
                content,
                "application/vnd.agentforge.environment+json",
                cancellationToken);
            var installation = await installations.ReadAsync(cancellationToken);
            await auditRecorder.RecordAsync(new AuditRecordRequest(
                installation.Id.Value == Guid.Empty ? null : installation.Id,
                request.ActorId,
                request.CorrelationId,
                null,
                "environment.profile-captured",
                AuditOutcome.Succeeded,
                new
                {
                    captured.Value.SchemaVersion,
                    captured.Value.ExecutableInventoryTruncated,
                },
                new
                {
                    captured.Value.Fingerprint,
                    ArtifactHash = artifact.ContentHash,
                    ExecutableCount = captured.Value.Executables.Count,
                    ManagerCount = captured.Value.Managers.Count,
                    AcceleratorCount = captured.Value.Accelerators.Count,
                    redacted.RedactionCount,
                },
                null), cancellationToken);
            var commit = await unitOfWork.CommitAsync(cancellationToken);
            return commit.Succeeded
                ? DomainResult.Success(new EnvironmentInventoryResult(
                    captured.Value,
                    artifact,
                    redacted.RedactionCount))
                : DomainResult.Fail<EnvironmentInventoryResult>(commit.Failure!);
        }
        catch (ArgumentException)
        {
            return DomainResult.Fail<EnvironmentInventoryResult>(new DomainFailure(
                FailureCode.BudgetExceeded,
                "Environment profile exceeds the configured redaction or artifact bound."));
        }
    }
}
