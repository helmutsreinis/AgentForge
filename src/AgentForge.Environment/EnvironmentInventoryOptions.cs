namespace AgentForge.Environment;

public sealed class EnvironmentInventoryOptions
{
    public const string SectionName = "AgentForge:EnvironmentInventory";

    public int MaximumPathDirectories { get; init; } = 128;

    public int MaximumFilesPerDirectory { get; init; } = 4096;

    public int MaximumExecutables { get; init; } = 4096;
}
