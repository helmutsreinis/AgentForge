namespace AgentForge.Mcp;

public sealed class McpExposureOptions
{
    public const string SectionName = "AgentForge:Mcp";

    public string[] AllowedTools { get; init; } = [];

    public string[] AllowedResources { get; init; } = [];

    public int MaximumResultCharacters { get; init; } = 16_384;
}
