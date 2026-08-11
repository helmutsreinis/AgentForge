namespace AgentForge.Tools;

public sealed class RestrictedProcessOptions
{
    public const string SectionName = "AgentForge:RestrictedProcess";

    public int MaximumArguments { get; init; } = 256;

    public int MaximumArgumentCharacters { get; init; } = 32_768;

    public int MaximumEnvironmentVariables { get; init; } = 64;

    public int MaximumEnvironmentValueCharacters { get; init; } = 8_192;

    public int MaximumTimeoutSeconds { get; init; } = 300;

    public int MaximumOutputBytes { get; init; } = 1_048_576;

    public int TerminationWaitSeconds { get; init; } = 10;

    public string[] AllowedInheritedEnvironmentVariables { get; init; } = [];

    public string[] AllowedInvocationEnvironmentVariables { get; init; } = [];
}
