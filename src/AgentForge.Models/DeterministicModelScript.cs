using AgentForge.Domain.Models;

namespace AgentForge.Models;

public sealed record DeterministicModelScript(
    IReadOnlyList<DeterministicModelStep> Steps,
    ModelFinishReason FinishReason = ModelFinishReason.Stop);

public abstract record DeterministicModelStep;

public sealed record DeterministicDelayStep(TimeSpan Delay) : DeterministicModelStep;

public sealed record DeterministicTextStep(string Delta) : DeterministicModelStep;

public sealed record DeterministicToolCallStep(
    string ToolCallId,
    string ToolName,
    IReadOnlyList<string> ArgumentDeltas) : DeterministicModelStep;

public sealed record DeterministicStructuredOutputStep(string Json) : DeterministicModelStep;

public sealed record DeterministicUsageStep(ModelUsage Usage) : DeterministicModelStep;

public sealed record DeterministicFailureStep(ModelProviderError Error) : DeterministicModelStep;
