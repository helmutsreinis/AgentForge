namespace AgentForge.Setup;

public sealed class InstallationOptions
{
    public const string SectionName = "AgentForge:Installation";

    public string? DataDirectory { get; set; }

    public string StateFileName { get; set; } = "installation-state.json";
}
