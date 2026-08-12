namespace AgentForge.Plugins;

public sealed class PluginOptions
{
    public const string SectionName = "AgentForge:Plugins";

    public string Directory { get; init; } = "plugins";

    public int MaximumPackages { get; init; } = 128;

    public int MaximumManifestBytes { get; init; } = 65_536;

    public long MaximumAssemblyBytes { get; init; } = 134_217_728;

    public string PluginWorkerExecutable { get; init; } = string.Empty;

    public Dictionary<string, string> TrustedPublicKeys { get; init; } = new(StringComparer.Ordinal);
}
