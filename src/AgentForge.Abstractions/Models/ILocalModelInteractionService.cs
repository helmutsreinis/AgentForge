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

public interface ILocalModelInteractionService
{
    Task<DomainResult<LocalModelInteractionResult>> InvokeAsync(
        LocalModelInteractionRequest request,
        CancellationToken cancellationToken);
}
