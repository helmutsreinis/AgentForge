using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;

namespace AgentForge.Abstractions.Models;

public sealed record LocalModelInteractionRequest(
    ModelRequestId RequestId,
    ProviderProfile Provider,
    string SystemInstruction,
    string Prompt,
    ModelInvocationLimits Limits,
    CorrelationId CorrelationId);

public sealed record LocalModelInteractionResult(
    ModelRequestId RequestId,
    string Text,
    ModelUsage? Usage,
    ModelFinishReason FinishReason,
    int ContextRedactionCount,
    int EventCount,
    string EvidenceHash);

public enum LocalModelInteractionProgressKind
{
    Started,
    TextDelta,
    Usage,
}

public sealed record LocalModelInteractionProgress(
    ModelRequestId RequestId,
    LocalModelInteractionProgressKind Kind,
    string? TextDelta = null,
    ModelUsage? Usage = null,
    int ContextRedactionCount = 0);

public interface ILocalModelInteractionObserver
{
    ValueTask OnProgressAsync(
        LocalModelInteractionProgress progress,
        CancellationToken cancellationToken);
}

public interface ILocalModelInteractionService
{
    Task<DomainResult<LocalModelInteractionResult>> InvokeAsync(
        LocalModelInteractionRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<LocalModelInteractionResult>> InvokeAsync(
        LocalModelInteractionRequest request,
        ILocalModelInteractionObserver observer,
        CancellationToken cancellationToken);
}
