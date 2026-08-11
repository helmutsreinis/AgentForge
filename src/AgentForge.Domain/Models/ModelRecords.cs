using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;

namespace AgentForge.Domain.Models;

public readonly record struct ModelRequestId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public enum ModelMessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

public enum ModelAttachmentModality
{
    Image,
    Audio,
    Document,
}

public enum ModelResponseFormatKind
{
    Text,
    JsonObject,
    JsonSchema,
}

public enum ModelCapability
{
    TextGeneration,
    Streaming,
    StructuredOutput,
    ToolCalls,
    ImageInput,
    AudioInput,
    DocumentInput,
}

public enum ModelCapabilityEvidenceSource
{
    Declared,
    Probed,
    Observed,
    Overridden,
    PolicyApproved,
}

public enum ModelCapabilityAvailability
{
    Available,
    Unavailable,
    Unknown,
    TemporarilyFailing,
}

public enum ModelProviderDataLocation
{
    InProcess,
    Loopback,
    PrivateNetwork,
    Cloud,
}

public enum ModelFinishReason
{
    Stop,
    Length,
    ToolCalls,
    ContentFilter,
}

public enum ModelProviderErrorCode
{
    InvalidRequest,
    UnsupportedCapability,
    AuthenticationFailed,
    RateLimited,
    ProviderUnavailable,
    InvalidResponse,
    BudgetExceeded,
    PolicyDenied,
}

public sealed record ModelAttachmentReference(
    string ArtifactHash,
    string MediaType,
    long Length,
    ModelAttachmentModality Modality,
    string? FileName);

public abstract record ModelContent;

public sealed record ModelTextContent(string Text) : ModelContent;

public sealed record ModelAttachmentContent(ModelAttachmentReference Attachment) : ModelContent;

public sealed record ModelToolCallContent(
    string ToolCallId,
    string ToolName,
    string ArgumentsJson) : ModelContent;

public sealed record ModelToolResultContent(
    string ToolCallId,
    string ToolName,
    string ResultJson,
    bool IsError) : ModelContent;

public sealed record ModelMessage(
    ModelMessageRole Role,
    IReadOnlyList<ModelContent> Content,
    string? Name = null);

public sealed record ModelToolDefinition(
    string Name,
    string Description,
    string InputSchemaJson);

public sealed record ModelResponseFormat(
    ModelResponseFormatKind Kind,
    string? JsonSchema = null);

public sealed record ModelInvocationLimits(
    int MaximumOutputTokens,
    int MaximumToolCalls,
    int MaximumEvents,
    int MaximumWallClockSeconds);

public sealed record ModelRequest(
    ModelRequestId Id,
    string Model,
    IReadOnlyList<ModelMessage> Messages,
    IReadOnlyList<ModelToolDefinition> Tools,
    ModelResponseFormat ResponseFormat,
    ModelInvocationLimits Limits,
    decimal? Temperature,
    decimal? TopP,
    int? Seed,
    CorrelationId CorrelationId,
    CorrelationId? CausationId = null);

public sealed record PreparedModelContext(
    ModelRequest Request,
    int RedactionCount,
    string Policy);

public sealed record ModelCapabilityEvidence(
    ModelCapability Capability,
    ModelCapabilityEvidenceSource Source,
    ModelCapabilityAvailability Availability,
    string Evidence,
    DateTimeOffset ObservedAt,
    DateTimeOffset? ExpiresAt = null);

public sealed record ModelProviderDescriptor(
    ProviderProfileId ProfileId,
    string ProviderType,
    string Model,
    IReadOnlyList<ModelCapabilityEvidence> Capabilities,
    ModelProviderRoutingEvidence? Routing = null);

public sealed record ModelProviderRoutingEvidence(
    ModelProviderDataLocation DataLocation,
    ModelCapabilityEvidenceSource Source,
    int MaximumContextTokens,
    int MaximumOutputTokens,
    int ReliabilityBasisPoints,
    decimal? InputCostPerMillionTokens,
    decimal? OutputCostPerMillionTokens,
    int TypicalLatencyMilliseconds,
    DateTimeOffset ObservedAt,
    DateTimeOffset? ExpiresAt = null);

public sealed record ModelRoutingRequest(
    ModelRequest Request,
    AgentModelPolicy Policy,
    long EstimatedInputTokens,
    IReadOnlyList<ProviderProfileId> ExcludedProfileIds);

public sealed record ModelRouteSelection(
    ProviderProfileId ProfileId,
    string ProviderType,
    string Model,
    bool IsFallback,
    IReadOnlySet<ModelCapability> RequiredCapabilities,
    string SelectionEvidenceHash);

public sealed record ModelUsage(
    long InputTokens,
    long OutputTokens,
    int ToolCalls,
    decimal? Cost,
    string? Currency);

public sealed record ModelProviderError(
    ModelProviderErrorCode Code,
    string Message,
    bool IsRetryable,
    int? StatusCode = null,
    TimeSpan? RetryAfter = null);

public abstract record ModelStreamEvent(
    ModelRequestId RequestId,
    long Sequence,
    DateTimeOffset Timestamp);

public sealed record ModelStartedEvent(
    ModelRequestId RequestId,
    long Sequence,
    DateTimeOffset Timestamp,
    ProviderProfileId ProviderProfileId,
    string ProviderType,
    string Model,
    string InputHash,
    string CapabilityEvidenceHash,
    int ContextRedactionCount = 0,
    string ContextPreparationPolicy = "none") : ModelStreamEvent(RequestId, Sequence, Timestamp);

public sealed record ModelTextDeltaEvent(
    ModelRequestId RequestId,
    long Sequence,
    DateTimeOffset Timestamp,
    string Delta) : ModelStreamEvent(RequestId, Sequence, Timestamp);

public sealed record ModelToolCallDeltaEvent(
    ModelRequestId RequestId,
    long Sequence,
    DateTimeOffset Timestamp,
    string ToolCallId,
    string? ToolName,
    string ArgumentsDelta) : ModelStreamEvent(RequestId, Sequence, Timestamp);

public sealed record ModelToolCallCompletedEvent(
    ModelRequestId RequestId,
    long Sequence,
    DateTimeOffset Timestamp,
    string ToolCallId,
    string ToolName,
    string ArgumentsJson) : ModelStreamEvent(RequestId, Sequence, Timestamp);

public sealed record ModelStructuredOutputEvent(
    ModelRequestId RequestId,
    long Sequence,
    DateTimeOffset Timestamp,
    string Json) : ModelStreamEvent(RequestId, Sequence, Timestamp);

public sealed record ModelUsageEvent(
    ModelRequestId RequestId,
    long Sequence,
    DateTimeOffset Timestamp,
    ModelUsage Usage) : ModelStreamEvent(RequestId, Sequence, Timestamp);

public sealed record ModelCompletedEvent(
    ModelRequestId RequestId,
    long Sequence,
    DateTimeOffset Timestamp,
    ModelFinishReason FinishReason) : ModelStreamEvent(RequestId, Sequence, Timestamp);

public sealed record ModelErrorEvent(
    ModelRequestId RequestId,
    long Sequence,
    DateTimeOffset Timestamp,
    ModelProviderError Error) : ModelStreamEvent(RequestId, Sequence, Timestamp);
