using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Memory;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Memory;
using AgentForge.Domain.Primitives;

namespace AgentForge.Memory;

internal sealed class MemoryService(
    IMemoryRepository repository,
    ISensitiveDataRedactor redactor,
    IAuditRecorder audit,
    IUnitOfWork unitOfWork,
    IClock clock) : IMemoryService
{
    public async Task<DomainResult<MemoryEntry>> CreateAsync(
        CreateMemoryRequest request,
        CancellationToken cancellationToken)
    {
        if (!ValidateRequest(request))
        {
            return Invalid<MemoryEntry>("Memory identity, scope, source, retention, or content bounds are invalid.");
        }

        var redacted = redactor.Redact(new { text = request.Content });
        using var redactedDocument = JsonDocument.Parse(redacted.Data.Json);
        var content = redactedDocument.RootElement.GetProperty("text").GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return Invalid<MemoryEntry>("Memory content is empty after redaction.");
        }

        var contentHash = Hash(content);
        var existing = await repository.FindByIdempotencyKeyAsync(
            request.InstallationId,
            request.IdempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            return existing.Id == request.Id && existing.ContentHash == contentHash &&
                existing.AgentId == request.AgentId && existing.ScopeId == request.ScopeId &&
                existing.Kind == request.Kind && existing.Source == request.Source &&
                existing.ExpiresAtUtc == request.ExpiresAtUtc
                ? DomainResult.Success(existing)
                : Conflict<MemoryEntry>("The memory idempotency key is bound to a different entry.");
        }

        var entry = new MemoryEntry(
            request.Id,
            request.InstallationId,
            request.AgentId,
            request.ScopeId.Trim(),
            request.Kind,
            content,
            contentHash,
            request.Source with { },
            clock.UtcNow,
            request.ExpiresAtUtc,
            0,
            request.ActorId,
            request.CorrelationId,
            request.CausationId,
            request.IdempotencyKey,
            redacted.RedactionCount);
        await repository.AddAsync(entry, cancellationToken);
        await audit.RecordAsync(new AuditRecordRequest(
            request.InstallationId,
            request.ActorId,
            request.CorrelationId,
            request.CausationId,
            "memory.created",
            AuditOutcome.Succeeded,
            new { EntryId = request.Id.ToString(), request.ScopeId, Kind = request.Kind.ToString() },
            new { entry.ContentHash, SourceHash = entry.Source.EvidenceHash, entry.ExpiresAtUtc, entry.RedactionCount },
            null), cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded ? DomainResult.Success(entry) : DomainResult.Fail<MemoryEntry>(commit.Failure!);
    }

    public async Task<DomainResult<IReadOnlyList<MemoryEntry>>> SearchAsync(
        MemoryQuery query,
        CancellationToken cancellationToken)
    {
        if (!ValidateQuery(query))
        {
            return Invalid<IReadOnlyList<MemoryEntry>>("Memory query scope, kinds, text, or bounds are invalid.");
        }

        var entries = await repository.SearchAsync(query with
        {
            ScopeId = query.ScopeId.Trim(),
            Text = query.Text.Trim(),
            Kinds = query.Kinds.Distinct().Order().ToImmutableArray(),
        }, cancellationToken);
        return DomainResult.Success(entries);
    }

    public async Task<DomainResult<bool>> DeleteAsync(
        DeleteMemoryRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Id.Value == Guid.Empty || request.InstallationId.Value == Guid.Empty ||
            request.AgentId.Value == Guid.Empty || !IsText(request.ScopeId, 256) ||
            !IsText(request.ActorId.Value, 256) || !IsText(request.CorrelationId.Value, 128))
        {
            return Invalid<bool>("Memory deletion identity or scope is invalid.");
        }

        var existing = await repository.FindByIdAsync(request.Id, cancellationToken);
        if (existing is null)
        {
            return DomainResult.Success(false);
        }

        if (existing.InstallationId != request.InstallationId || existing.AgentId != request.AgentId ||
            !string.Equals(existing.ScopeId, request.ScopeId.Trim(), StringComparison.Ordinal))
        {
            return DomainResult.Fail<bool>(new DomainFailure(FailureCode.PolicyDenied, "Memory deletion scope does not match."));
        }

        await repository.DeleteAsync(request.Id, cancellationToken);
        await audit.RecordAsync(new AuditRecordRequest(
            request.InstallationId,
            request.ActorId,
            request.CorrelationId,
            request.CausationId,
            "memory.deleted",
            AuditOutcome.Succeeded,
            new { EntryId = request.Id.ToString(), request.ScopeId },
            new { existing.ContentHash, Kind = existing.Kind.ToString() },
            null), cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded ? DomainResult.Success(true) : DomainResult.Fail<bool>(commit.Failure!);
    }

    private bool ValidateRequest(CreateMemoryRequest request)
    {
        if (request is null || request.Id.Value == Guid.Empty || request.InstallationId.Value == Guid.Empty ||
            request.AgentId.Value == Guid.Empty || !IsText(request.ScopeId, 256) ||
            !IsText(request.ActorId.Value, 256) || !IsText(request.CorrelationId.Value, 128) ||
            !IsText(request.IdempotencyKey, 128) || request.Content is null || request.Content.Any(character => character == '\0') ||
            request.Source is null || !IsText(request.Source.SourceId, 512) || !IsHash(request.Source.EvidenceHash) ||
            request.ExpiresAtUtc.Offset != TimeSpan.Zero || request.ExpiresAtUtc <= clock.UtcNow)
        {
            return false;
        }

        var maximumLength = request.Kind == MemoryKind.Working ? 16_384 : 65_536;
        var maximumRetention = request.Kind == MemoryKind.Working ? TimeSpan.FromDays(1) : TimeSpan.FromDays(3650);
        if (request.Content.Length is < 1 || request.Content.Length > maximumLength ||
            request.ExpiresAtUtc - clock.UtcNow > maximumRetention)
        {
            return false;
        }

        return request.Kind switch
        {
            MemoryKind.Working => request.Source.Kind is MemorySourceKind.UserInput or MemorySourceKind.TaskEvidence,
            MemoryKind.Task => request.Source.Kind is MemorySourceKind.TaskEvidence or MemorySourceKind.UserInput,
            MemoryKind.Episodic => request.Source.Kind == MemorySourceKind.Trajectory,
            MemoryKind.Semantic => request.Source.Kind == MemorySourceKind.SearchCitation,
            MemoryKind.User => request.Source.Kind is MemorySourceKind.UserInput or MemorySourceKind.UserCorrection,
            MemoryKind.Environment => request.Source.Kind == MemorySourceKind.EnvironmentProfile,
            MemoryKind.Procedural => request.Source.Kind is MemorySourceKind.SkillReceipt or MemorySourceKind.UserCorrection,
            _ => false,
        };
    }

    private static bool ValidateQuery(MemoryQuery query) =>
        query is not null && query.InstallationId.Value != Guid.Empty && query.AgentId.Value != Guid.Empty &&
        IsText(query.ScopeId, 256) && IsText(query.Text, 256) &&
        query.Kinds.Length is >= 1 and <= 7 && query.Kinds.Distinct().Count() == query.Kinds.Length &&
        query.MaximumResults is >= 1 and <= 50 && query.AsOfUtc.Offset == TimeSpan.Zero;

    private static bool IsText(string value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);

    private static bool IsHash(string value)
    {
        if (value is not { Length: 71 } || !value.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value.AsSpan(7))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Hash(string value) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";

    private static DomainResult<T> Invalid<T>(string message) => DomainResult.Fail<T>(
        new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<T> Conflict<T>(string message) => DomainResult.Fail<T>(
        new DomainFailure(FailureCode.ConcurrencyConflict, message));
}
