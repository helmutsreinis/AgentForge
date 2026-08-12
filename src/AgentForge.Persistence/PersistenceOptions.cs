namespace AgentForge.Persistence;

public enum PersistenceProvider
{
    Sqlite,
    PostgreSql,
}

public sealed class PersistenceOptions
{
    public const string SectionName = "AgentForge:Persistence";

    public string DatabaseFileName { get; set; } = "agentforge.db";

    public string ArtifactDirectoryName { get; set; } = "artifacts";

    public bool EnableConnectionPooling { get; set; } = true;

    public PersistenceProvider Provider { get; set; } = PersistenceProvider.Sqlite;

    public string PostgreSqlConnectionStringEnvironmentVariable { get; set; } =
        "AGENTFORGE_POSTGRESQL_CONNECTION";

    public string PostgreSqlDumpExecutable { get; set; } = string.Empty;

    public string PostgreSqlRestoreExecutable { get; set; } = string.Empty;
}
