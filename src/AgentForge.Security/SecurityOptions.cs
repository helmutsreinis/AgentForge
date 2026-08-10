namespace AgentForge.Security;

public sealed class SecurityOptions
{
    public const string SectionName = "AgentForge:Security";

    public int MaximumRedactionPayloadBytes { get; set; } = 1_048_576;

    public int MaximumRedactionDepth { get; set; } = 32;
}
