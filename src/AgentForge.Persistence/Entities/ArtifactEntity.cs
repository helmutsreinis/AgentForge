namespace AgentForge.Persistence.Entities;

internal sealed class ArtifactEntity
{
    public string ContentHash { get; set; } = string.Empty;

    public long Length { get; set; }

    public string MediaType { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public string RelativePath { get; set; } = string.Empty;
}
