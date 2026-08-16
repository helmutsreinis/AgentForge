using System.Security.Cryptography;
using System.Text;
using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Security;
using AgentForge.Domain.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Tools;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentForgeTools(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RestrictedProcessOptions>()
            .Bind(configuration.GetSection(RestrictedProcessOptions.SectionName))
            .Validate(options => options.MaximumArguments is >= 1 and <= 4096)
            .Validate(options => options.MaximumArgumentCharacters is >= 1 and <= 1_048_576)
            .Validate(options => options.MaximumEnvironmentVariables is >= 0 and <= 1024)
            .Validate(options => options.MaximumEnvironmentValueCharacters is >= 1 and <= 1_048_576)
            .Validate(options => options.MaximumTimeoutSeconds is >= 1 and <= 3600)
            .Validate(options => options.MaximumOutputBytes is >= 1 and <= 16_777_216)
            .Validate(options => options.TerminationWaitSeconds is >= 1 and <= 60)
            .Validate(options => AreValidNames(options.AllowedInheritedEnvironmentVariables))
            .Validate(options => AreValidNames(options.AllowedInvocationEnvironmentVariables))
            .ValidateOnStart();
        services.AddOptions<DockerSandboxOptions>()
            .Bind(configuration.GetSection(DockerSandboxOptions.SectionName))
            .Validate(options =>
                (string.IsNullOrWhiteSpace(options.RuntimeExecutable) && string.IsNullOrWhiteSpace(options.ImageReference)) ||
                DockerContainerSandbox.IsConfigured(options))
            .ValidateOnStart();
        services.AddSingleton<RestrictedHostSandbox>();
        services.AddSingleton<IContainerRuntimeInvoker, RestrictedHostContainerRuntimeInvoker>();
        services.AddSingleton<DockerContainerSandbox>();
        services.AddSingleton<ISandbox, SelectingSandbox>();
        services.AddSingleton<IToolCatalog>(_ => ToolCatalog.Create(BuiltInTools()).Value);
        services.AddScoped<IToolInvocationPlanner, ToolInvocationPlanner>();
        services.AddScoped<IBuiltInToolExecutor, BuiltInWorkspaceToolExecutor>();
        services.AddScoped<IToolInvocationService, ToolInvocationService>();
        services.AddScoped<IToolAvailabilityProbeService, ToolAvailabilityProbeService>();
        return services;
    }

    private static bool AreValidNames(string[]? names)
    {
        return names is not null && names.Length <= 256 && names.All(IsValidName) &&
            names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length;
    }

    private static bool IsValidName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static IReadOnlyList<ToolDescriptorDefinition> BuiltInTools()
    {
        var executable = Environment.ProcessPath ?? typeof(ServiceCollectionExtensions).Assembly.Location;
        var provenance = new ToolProvenance(
            ToolCatalogSourceKind.BuiltIn,
            ToolTrustLevel.BuiltIn,
            "agentforge.workspace-tools",
            "1.0.0",
            Hash("AgentForge managed workspace tools v1"));
        var isolation = ProcessIsolationFeature.WorkingDirectoryContainment |
            ProcessIsolationFeature.BoundedOutput |
            ProcessIsolationFeature.NetworkIsolation |
            ProcessIsolationFeature.FileSystemIsolation;

        return
        [
            new ToolDescriptorDefinition(
                "tool:workspace.list",
                "1.0.0",
                "List workspace directory",
                "List bounded metadata for direct children of one approved workspace directory.",
                "Returns names, kinds, and file sizes without following links, reading file bodies, starting a process, or using the network.",
                "tool:workspace.read",
                CapabilityRiskClass.Read,
                AuthorizationTargetKind.FileSystemPath,
                "directory",
                ToolSideEffectKind.ReadsFileSystem,
                ToolOutputSensitivity.LocalMetadata,
                [
                    new ToolParameterDescriptor(
                        "directory", ToolParameterType.Text, true, 2048, null, null, [],
                        "Existing absolute directory inside the exact workspace."),
                    new ToolParameterDescriptor(
                        "maximumEntries", ToolParameterType.WholeNumber, true, 0, 1, 500, [],
                        "Maximum direct children to return."),
                ],
                new ToolProcessDefinition(
                    executable, [],
                    [
                        new ToolArgumentBinding(ToolArgumentBindingKind.Positional, "directory", null),
                        new ToolArgumentBinding(ToolArgumentBindingKind.Positional, "maximumEntries", null),
                    ],
                    [], ProcessSandboxKind.BuiltIn, ProcessNetworkPolicy.Denied, isolation, 10, 262_144),
                provenance,
                ExecutionKind: ToolExecutionKind.BuiltIn,
                BuiltInHandlerId: "workspace.list"),
            new ToolDescriptorDefinition(
                "tool:workspace.read-text",
                "1.0.0",
                "Read workspace text",
                "Read one exact bounded UTF-8 file inside the approved workspace.",
                "Rejects links, binary text, files beyond the approved byte limit, child processes, environment access, and network access.",
                "tool:workspace.read",
                CapabilityRiskClass.Read,
                AuthorizationTargetKind.FileSystemPath,
                "path",
                ToolSideEffectKind.ReadsFileSystem,
                ToolOutputSensitivity.PotentiallySensitive,
                [
                    new ToolParameterDescriptor(
                        "path", ToolParameterType.Text, true, 2048, null, null, [],
                        "Existing absolute UTF-8 file inside the exact workspace."),
                    new ToolParameterDescriptor(
                        "maximumBytes", ToolParameterType.WholeNumber, true, 0, 1, 65_536, [],
                        "Maximum file bytes allowed for this exact invocation."),
                ],
                new ToolProcessDefinition(
                    executable, [],
                    [
                        new ToolArgumentBinding(ToolArgumentBindingKind.Positional, "path", null),
                        new ToolArgumentBinding(ToolArgumentBindingKind.Positional, "maximumBytes", null),
                    ],
                    [], ProcessSandboxKind.BuiltIn, ProcessNetworkPolicy.Denied, isolation, 10, 65_536),
                provenance,
                ExecutionKind: ToolExecutionKind.BuiltIn,
                BuiltInHandlerId: "workspace.read-text"),
        ];
    }

    private static string Hash(string value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";
}
