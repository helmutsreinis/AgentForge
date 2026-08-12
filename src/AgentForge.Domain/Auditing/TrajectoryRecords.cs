using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain.Artifacts;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Auditing;

public enum TrajectoryStage
{
    Setup,
    Intake,
    Context,
    Snapshot,
    ModelCall,
    ToolCall,
    Retry,
    Verification,
    StateTransition,
    Delivery,
    Learning,
    Other,
}

public sealed record TrajectoryExportRequest(
    InstallationId InstallationId,
    long AfterSequence,
    int MaximumEvents,
    CorrelationId? CorrelationFilter,
    ActorId ActorId,
    string IdempotencyKey,
    CorrelationId CorrelationId,
    CorrelationId? CausationId = null);

public sealed record TrajectoryExportReceipt(
    Guid ExportId,
    InstallationId InstallationId,
    long FirstSequence,
    long LastSequence,
    int EventCount,
    string AuditHeadHash,
    string ExportHash,
    ArtifactReference Artifact,
    DateTimeOffset CreatedAt,
    ActorId ActorId,
    string RequestHash,
    string IdempotencyKey,
    CorrelationId CorrelationId,
    CorrelationId? CausationId);

public static class TrajectoryExportValidation
{
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    public static DomainResult<bool> Validate(TrajectoryExportRequest? request)
    {
        if (request is null || request.InstallationId.Value == Guid.Empty || request.AfterSequence < 0 ||
            request.MaximumEvents is < 1 or > 100_000 || !IsText(request.ActorId.Value, 256) ||
            !IsText(request.IdempotencyKey, 128) || !IsText(request.CorrelationId.Value, 128) ||
            request.CorrelationFilter is { } filter && !IsText(filter.Value, 128) ||
            request.CausationId is { } causation && !IsText(causation.Value, 128))
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.ValidationFailure, "Trajectory export request is invalid or exceeds a bound."));
        return DomainResult.Success(true);
    }

    public static DomainResult<bool> ValidateReceipt(TrajectoryExportReceipt? receipt)
    {
        if (receipt is null || receipt.ExportId == Guid.Empty || receipt.InstallationId.Value == Guid.Empty ||
            receipt.FirstSequence < 0 || receipt.LastSequence < receipt.FirstSequence || receipt.EventCount < 0 ||
            receipt.EventCount == 0 != (receipt.FirstSequence == 0 && receipt.LastSequence == 0) ||
            receipt.EventCount > 0 && receipt.FirstSequence == 0 || receipt.AuditHeadHash.Length != 64 ||
            receipt.AuditHeadHash.AsSpan().IndexOfAnyExcept(LowerHex) >= 0 || !IsHash(receipt.ExportHash) ||
            receipt.Artifact is null || !string.Equals(
                receipt.Artifact.ContentHash, receipt.ExportHash, StringComparison.Ordinal) ||
            receipt.Artifact.Length <= 0 || receipt.Artifact.CreatedAt == default || receipt.CreatedAt == default ||
            !IsText(receipt.ActorId.Value, 256) || !IsHash(receipt.RequestHash) ||
            !IsText(receipt.IdempotencyKey, 128) || !IsText(receipt.CorrelationId.Value, 128) ||
            receipt.CausationId is { } causation && !IsText(causation.Value, 128))
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.ValidationFailure, "Trajectory export receipt is invalid or has lost integrity."));
        return DomainResult.Success(true);
    }

    public static string ComputeRequestHash(TrajectoryExportRequest request)
    {
        var builder = new StringBuilder(1024);
        foreach (var value in new[]
        {
            request.InstallationId.ToString(), request.AfterSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.MaximumEvents.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.CorrelationFilter?.Value ?? string.Empty, request.ActorId.Value,
            request.CorrelationId.Value, request.CausationId?.Value ?? string.Empty,
        })
        {
            builder.Append(value.Length).Append(':').Append(value);
        }
        return Hash(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    public static string Hash(ReadOnlySpan<byte> bytes) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";

    public static bool IsHash(string value) => value.Length == 71 &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept(LowerHex) < 0;

    private static bool IsText(string value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(char.IsControl);
}

public static class TrajectoryStageClassifier
{
    public static TrajectoryStage Classify(string operationType)
    {
        var value = operationType.ToLowerInvariant();
        if (value.Contains("setup", StringComparison.Ordinal))
            return TrajectoryStage.Setup;
        if (value.Contains("intake", StringComparison.Ordinal) || value.Contains("task.create", StringComparison.Ordinal))
            return TrajectoryStage.Intake;
        if (value.Contains("context", StringComparison.Ordinal) || value.Contains("memory", StringComparison.Ordinal))
            return TrajectoryStage.Context;
        if (value.Contains("snapshot", StringComparison.Ordinal) || value.Contains("checkpoint", StringComparison.Ordinal))
            return TrajectoryStage.Snapshot;
        if (value.Contains("retry", StringComparison.Ordinal) || value.Contains("failover", StringComparison.Ordinal))
            return TrajectoryStage.Retry;
        if (value.Contains("model", StringComparison.Ordinal) || value.Contains("provider.call", StringComparison.Ordinal))
            return TrajectoryStage.ModelCall;
        if (value.Contains("tool", StringComparison.Ordinal) || value.Contains("process", StringComparison.Ordinal))
            return TrajectoryStage.ToolCall;
        if (value.Contains("verif", StringComparison.Ordinal) || value.Contains("review", StringComparison.Ordinal) ||
            value.Contains("test", StringComparison.Ordinal))
            return TrajectoryStage.Verification;
        if (value.Contains("transition", StringComparison.Ordinal) || value.Contains("complete", StringComparison.Ordinal) ||
            value.Contains("cancel", StringComparison.Ordinal) || value.Contains("recover", StringComparison.Ordinal))
            return TrajectoryStage.StateTransition;
        if (value.Contains("channel", StringComparison.Ordinal) || value.Contains("delivery", StringComparison.Ordinal))
            return TrajectoryStage.Delivery;
        if (value.Contains("learn", StringComparison.Ordinal) || value.Contains("skill", StringComparison.Ordinal) ||
            value.Contains("bundle", StringComparison.Ordinal))
            return TrajectoryStage.Learning;
        return TrajectoryStage.Other;
    }
}
