namespace AgentForge.Tools;

public sealed class DockerSandboxOptions
{
    public const string SectionName = "AgentForge:DockerSandbox";

    public string RuntimeExecutable { get; init; } = string.Empty;

    public string ImageReference { get; init; } = string.Empty;

    public string ContainerUser { get; init; } = "65532:65532";

    public int MemoryMegabytes { get; init; } = 512;

    public decimal CpuLimit { get; init; } = 1m;

    public int ProcessLimit { get; init; } = 128;

    public int TemporaryMegabytes { get; init; } = 64;

    public int CleanupTimeoutSeconds { get; init; } = 30;

    public Dictionary<string, string> ExecutableMappings { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["agentforge-plugin-worker"] = "/opt/agentforge/agentforge-plugin-worker",
        ["agentforge-plugin-worker.exe"] = "/opt/agentforge/agentforge-plugin-worker",
        ["dotnet"] = "/usr/bin/dotnet",
        ["dotnet.exe"] = "/usr/bin/dotnet",
        ["git"] = "/usr/bin/git",
        ["git.exe"] = "/usr/bin/git",
    };
}
