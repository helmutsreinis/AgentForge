namespace AgentForge.Persistence;

public sealed class PersistenceOptions
{
    public const string SectionName = "AgentForge:Persistence";

    public string DatabaseFileName { get; set; } = "agentforge.db";

    public string ArtifactDirectoryName { get; set; } = "artifacts";

    public bool EnableConnectionPooling { get; set; } = true;
}
