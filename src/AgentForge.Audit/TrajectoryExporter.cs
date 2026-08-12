using System.Text.Json;
using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Primitives;

namespace AgentForge.Audit;

internal sealed class TrajectoryExporter(
    IAuditReader reader,
    IAuditIntegrityVerifier integrityVerifier,
    IAuditRecorder recorder,
    IArtifactStore artifactStore,
    ITrajectoryExportRepository repository,
    IInstallationStateReader installationReader,
    ISensitiveDataRedactor redactor,
    IUnitOfWork unitOfWork,
    IClock clock) : ITrajectoryExporter
{
    private const int PageSize = 1000;

    public async Task<DomainResult<TrajectoryExportReceipt>> ExportAsync(
        TrajectoryExportRequest request,
        CancellationToken cancellationToken)
    {
        var validation = TrajectoryExportValidation.Validate(request);
        if (!validation.IsSuccess) return DomainResult.Fail<TrajectoryExportReceipt>(validation.Failure!);
        var requestHash = TrajectoryExportValidation.ComputeRequestHash(request);
        var existing = await repository.GetByIdempotencyAsync(
            request.InstallationId, request.IdempotencyKey, cancellationToken);
        if (existing is not null)
            return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                ? DomainResult.Success(existing)
                : Failure(FailureCode.ConcurrencyConflict,
                    "Trajectory idempotency key is already bound to a different request.");
        var installation = await installationReader.ReadAsync(cancellationToken);
        if (installation.Id != request.InstallationId)
            return Failure(FailureCode.PolicyDenied, "Trajectory installation scope does not match current authority.");
        var integrity = await integrityVerifier.VerifyAsync(cancellationToken);
        if (!integrity.IsValid)
            return Failure(FailureCode.ValidationFailure, "Audit integrity verification failed; export is blocked.");

        var events = await ReadEventsAsync(request, cancellationToken);
        if (!events.IsSuccess) return DomainResult.Fail<TrajectoryExportReceipt>(events.Failure!);
        var createdAt = clock.UtcNow;
        var bytes = Serialize(request, events.Value, integrity, createdAt);
        var exportHash = TrajectoryExportValidation.Hash(bytes);
        await using var stream = new MemoryStream(bytes, writable: false);
        var artifact = await artifactStore.PutAsync(
            stream, "application/vnd.agentforge.trajectory+json", cancellationToken);
        var receipt = new TrajectoryExportReceipt(
            Guid.NewGuid(), request.InstallationId,
            events.Value.Count == 0 ? 0 : events.Value[0].Sequence,
            events.Value.Count == 0 ? 0 : events.Value[^1].Sequence,
            events.Value.Count, integrity.HeadHash, exportHash, artifact, createdAt,
            request.ActorId, requestHash, request.IdempotencyKey, request.CorrelationId, request.CausationId);
        await repository.AddAsync(receipt, cancellationToken);
        await recorder.RecordAsync(new AuditRecordRequest(
            request.InstallationId, request.ActorId, request.CorrelationId, request.CausationId,
            "trajectory.export", AuditOutcome.Succeeded,
            new { request.AfterSequence, request.MaximumEvents, correlationFilter = request.CorrelationFilter?.Value },
            new { receipt.ExportId, receipt.EventCount, receipt.ExportHash, artifact.ContentHash }, null),
            cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded
            ? DomainResult.Success(receipt)
            : DomainResult.Fail<TrajectoryExportReceipt>(commit.Failure!);
    }

    private async Task<DomainResult<IReadOnlyList<ExportEvent>>> ReadEventsAsync(
        TrajectoryExportRequest request,
        CancellationToken cancellationToken)
    {
        var events = new List<ExportEvent>(Math.Min(request.MaximumEvents, PageSize));
        var after = request.AfterSequence;
        var scanned = 0;
        while (events.Count < request.MaximumEvents && scanned < 100_000)
        {
            var page = await reader.ReadAsync(request.InstallationId, after, PageSize, cancellationToken);
            if (page.Count == 0) break;
            foreach (var item in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;
                after = item.Sequence;
                if (request.CorrelationFilter is { } filter && item.CorrelationId != filter) continue;
                using var input = JsonDocument.Parse(item.Input.Json);
                using var output = JsonDocument.Parse(item.Output.Json);
                var redactedInput = redactor.Redact(input.RootElement);
                var redactedOutput = redactor.Redact(output.RootElement);
                events.Add(new ExportEvent(
                    item.EventId, item.Sequence, item.Timestamp, item.ActorId.Value,
                    item.CorrelationId.Value, item.CausationId?.Value, item.OperationType,
                    TrajectoryStageClassifier.Classify(item.OperationType), item.Outcome,
                    redactedInput.Data.Json, redactedOutput.Data.Json, item.ErrorClassification,
                    item.PreviousHash, item.EventHash,
                    redactedInput.RedactionCount + redactedOutput.RedactionCount));
                if (events.Count == request.MaximumEvents) break;
            }
            if (page.Count < PageSize) break;
        }
        if (scanned >= 100_000 && events.Count < request.MaximumEvents)
            return DomainResult.Fail<IReadOnlyList<ExportEvent>>(new DomainFailure(
                FailureCode.BudgetExceeded, "Trajectory correlation scan exceeded its event bound."));
        return DomainResult.Success<IReadOnlyList<ExportEvent>>(events);
    }

    private static byte[] Serialize(
        TrajectoryExportRequest request,
        IReadOnlyList<ExportEvent> events,
        AuditVerificationResult integrity,
        DateTimeOffset createdAt)
    {
        using var memory = new MemoryStream();
        using var writer = new Utf8JsonWriter(memory, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("installationId", request.InstallationId.Value);
        writer.WriteString("createdAt", createdAt);
        writer.WriteNumber("afterSequence", request.AfterSequence);
        writer.WriteString("correlationFilter", request.CorrelationFilter?.Value);
        writer.WriteNumber("verifiedAuditEventCount", integrity.VerifiedEventCount);
        writer.WriteString("auditHeadHash", integrity.HeadHash);
        writer.WriteStartArray("events");
        foreach (var item in events)
        {
            writer.WriteStartObject();
            writer.WriteString("eventId", item.EventId);
            writer.WriteNumber("sequence", item.Sequence);
            writer.WriteString("timestamp", item.Timestamp);
            writer.WriteString("actorId", item.ActorId);
            writer.WriteString("correlationId", item.CorrelationId);
            writer.WriteString("causationId", item.CausationId);
            writer.WriteString("operationType", item.OperationType);
            writer.WriteString("stage", item.Stage.ToString());
            writer.WriteString("outcome", item.Outcome.ToString());
            writer.WritePropertyName("input");
            writer.WriteRawValue(item.InputJson, skipInputValidation: false);
            writer.WritePropertyName("output");
            writer.WriteRawValue(item.OutputJson, skipInputValidation: false);
            writer.WriteString("errorClassification", item.ErrorClassification);
            writer.WriteString("previousHash", item.PreviousHash);
            writer.WriteString("eventHash", item.EventHash);
            writer.WriteNumber("secondaryRedactionCount", item.SecondaryRedactionCount);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return memory.ToArray();
    }

    private static DomainResult<TrajectoryExportReceipt> Failure(FailureCode code, string message) =>
        DomainResult.Fail<TrajectoryExportReceipt>(new DomainFailure(code, message));

    private sealed record ExportEvent(
        Guid EventId,
        long Sequence,
        DateTimeOffset Timestamp,
        string ActorId,
        string CorrelationId,
        string? CausationId,
        string OperationType,
        TrajectoryStage Stage,
        AuditOutcome Outcome,
        string InputJson,
        string OutputJson,
        string? ErrorClassification,
        string PreviousHash,
        string EventHash,
        int SecondaryRedactionCount);
}
