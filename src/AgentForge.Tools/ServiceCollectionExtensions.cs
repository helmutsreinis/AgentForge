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
        services.AddScoped<IBuiltInToolHandler, BuiltInWorkspaceToolHandler>();
        services.AddScoped<IBuiltInToolExecutor, BuiltInToolExecutor>();
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
            new ToolDescriptorDefinition(
                "tool:search.brave",
                "1.0.0",
                "Search the web with Brave",
                "Search Brave through AgentForge's configured, credential-isolated research provider.",
                "Returns bounded citations from one exact operator-approved query. The model never receives the API credential and cannot select another endpoint.",
                "tool:search.web",
                CapabilityRiskClass.Credential,
                AuthorizationTargetKind.Uri,
                "endpoint",
                ToolSideEffectKind.ReadsNetwork | ToolSideEffectKind.CredentialAccess,
                ToolOutputSensitivity.Public,
                [
                    new ToolParameterDescriptor(
                        "query", ToolParameterType.Text, true, 512, null, null, [],
                        "Exact search query to submit after operator approval."),
                    new ToolParameterDescriptor(
                        "maximumResults", ToolParameterType.WholeNumber, true, 0, 1, 10, [],
                        "Maximum normalized citations to return."),
                    new ToolParameterDescriptor(
                        "endpoint", ToolParameterType.Text, true, 2048, null, null,
                        ["https://api.search.brave.com/res/v1/web/search"],
                        "Fixed AgentForge-managed Brave Search endpoint."),
                ],
                new ToolProcessDefinition(
                    executable, [],
                    [
                        new ToolArgumentBinding(ToolArgumentBindingKind.Positional, "query", null),
                        new ToolArgumentBinding(ToolArgumentBindingKind.Positional, "maximumResults", null),
                        new ToolArgumentBinding(ToolArgumentBindingKind.Positional, "endpoint", null),
                    ],
                    [], ProcessSandboxKind.BuiltIn,
                    ProcessNetworkPolicy.FixedEndpointOnly,
                    ProcessIsolationFeature.BoundedOutput | ProcessIsolationFeature.NetworkIsolation,
                    30, 65_536),
                new ToolProvenance(
                    ToolCatalogSourceKind.BuiltIn,
                    ToolTrustLevel.BuiltIn,
                    "agentforge.brave-search",
                    "1.0.0",
                    Hash("AgentForge managed Brave Search tool v1")),
                ExecutionKind: ToolExecutionKind.BuiltIn,
                BuiltInHandlerId: "search.brave"),
            new ToolDescriptorDefinition(
                "tool:http-api.get",
                "1.0.0",
                "Read a configured HTTP API",
                "Issue one bounded GET through an operator-configured HTTPS API profile for an active generated skill.",
                "The bearer token stays OS-backed. Profile, path, query, byte limit, and exact final endpoint are bound to a single-use approval.",
                "tool:http-api.read",
                CapabilityRiskClass.Credential,
                AuthorizationTargetKind.Uri,
                "endpoint",
                ToolSideEffectKind.ReadsNetwork | ToolSideEffectKind.CredentialAccess,
                ToolOutputSensitivity.PotentiallySensitive,
                [
                    new ToolParameterDescriptor(
                        "profileId", ToolParameterType.Text, true, 64, null, null, [],
                        "Configured bearer-authenticated HTTP API profile ID."),
                    new ToolParameterDescriptor(
                        "relativePath", ToolParameterType.Text, true, 2048, null, null, [],
                        "Relative path contained by the configured base endpoint."),
                    new ToolParameterDescriptor(
                        "queryJson", ToolParameterType.Text, true, 8192, null, null, [],
                        "Canonical bounded JSON object containing scalar query values."),
                    new ToolParameterDescriptor(
                        "maximumResponseBytes", ToolParameterType.WholeNumber, true, 0, 1, 1_048_576, [],
                        "Maximum UTF-8 response bytes accepted from the endpoint."),
                    new ToolParameterDescriptor(
                        "endpoint", ToolParameterType.Text, true, 2048, null, null, [],
                        "Server-resolved exact endpoint derived from the configured profile."),
                ],
                new ToolProcessDefinition(
                    executable, [],
                    [
                        new ToolArgumentBinding(ToolArgumentBindingKind.Positional, "profileId", null),
                        new ToolArgumentBinding(ToolArgumentBindingKind.Positional, "relativePath", null),
                        new ToolArgumentBinding(ToolArgumentBindingKind.Positional, "queryJson", null),
                        new ToolArgumentBinding(ToolArgumentBindingKind.Positional, "maximumResponseBytes", null),
                        new ToolArgumentBinding(ToolArgumentBindingKind.Positional, "endpoint", null),
                    ],
                    [], ProcessSandboxKind.BuiltIn,
                    ProcessNetworkPolicy.FixedEndpointOnly,
                    ProcessIsolationFeature.BoundedOutput | ProcessIsolationFeature.NetworkIsolation,
                    30, 1_048_576),
                new ToolProvenance(
                    ToolCatalogSourceKind.BuiltIn,
                    ToolTrustLevel.BuiltIn,
                    "agentforge.generated-skill-http-api",
                    "1.0.0",
                    Hash("AgentForge managed generated-skill HTTP API GET v1")),
                ExecutionKind: ToolExecutionKind.BuiltIn,
                BuiltInHandlerId: "http-api.get"),
        ];
    }

    private static string Hash(string value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";
}
