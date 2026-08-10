using System.Net;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Setup;
using AgentForge.Audit;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Setup;
using AgentForge.Persistence;
using AgentForge.Security;
using AgentForge.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    PrintHelp();
    return 0;
}

if (args is ["setup", "begin", .. var beginArguments])
{
    return await BeginSetupAsync(beginArguments);
}

if (args is ["setup", "agent", "preview", .. var previewArguments])
{
    return await ConfigureAgentAsync(previewArguments, create: false);
}

if (args is ["setup", "agent", "create", .. var createArguments])
{
    return await ConfigureAgentAsync(createArguments, create: true);
}

if (args is ["setup", "complete", .. var completeArguments])
{
    return await CompleteSetupAsync(completeArguments);
}

var endpoint = Environment.GetEnvironmentVariable("AGENTFORGE_ENDPOINT") ?? "http://127.0.0.1:5047";
if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseAddress))
{
    await Console.Error.WriteLineAsync("AGENTFORGE_ENDPOINT must be an absolute URI.");
    return 1;
}

var path = args switch
{
    ["status"] => "/api/v1/status",
    ["setup", "status"] => "/api/v1/setup/status",
    _ => null,
};

if (path is null)
{
    await Console.Error.WriteLineAsync("Unknown command.");
    PrintHelp();
    return 1;
}

using var client = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(10) };
try
{
    using var response = await client.GetAsync(path);
    var payload = await response.Content.ReadAsStringAsync();
    await Console.Out.WriteLineAsync(payload);

    return response.StatusCode switch
    {
        HttpStatusCode.OK => 0,
        HttpStatusCode.ServiceUnavailable => 2,
        _ => 1,
    };
}
catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
{
    await Console.Error.WriteLineAsync($"AgentForge host is unavailable: {exception.Message}");
    return 1;
}

static async Task<int> BeginSetupAsync(string[] arguments)
{
    if (arguments is ["--interactive"])
    {
        arguments = await ReadInteractiveArgumentsAsync();
    }

    if (!TryParseBeginOptions(arguments, out var options, out var error))
    {
        await Console.Error.WriteLineAsync(error);
        return 1;
    }

    string dataDirectory;
    try
    {
        dataDirectory = Path.GetFullPath(options!.DataDirectory);
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
    {
        await Console.Error.WriteLineAsync("--data-directory is not a valid filesystem path.");
        return 1;
    }

    var settings = new Dictionary<string, string?>
    {
        ["AgentForge:Installation:DataDirectory"] = dataDirectory,
    };
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(settings)
        .Build();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddAgentForgeSetup(configuration);
    services.AddAgentForgePersistence(configuration);
    services.AddAgentForgeSecurity(configuration);
    services.AddAgentForgeAudit();

    await using var provider = services.BuildServiceProvider(validateScopes: true);
    await using var scope = provider.CreateAsyncScope();
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;
    try
    {
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync(cancellation.Token);
        var result = await scope.ServiceProvider.GetRequiredService<ISetupApplicationService>()
            .BeginAsync(new BeginSetupRequest(
                options.InstallationId,
                new ActorId(options.ActorId),
                new CorrelationId(options.CorrelationId)), cancellation.Token);
        if (!result.IsSuccess)
        {
            await WriteJsonAsync(new
            {
                succeeded = false,
                failure = new
                {
                    code = result.Failure!.Code.ToString(),
                    result.Failure.Message,
                    result.Failure.IsRetryable,
                },
            });
            return result.Failure!.Code is FailureCode.ConcurrencyConflict ? 3 : 1;
        }

        var completed = result.Value;
        await WriteJsonAsync(new
        {
            succeeded = true,
            installationId = completed.Installation.Id.ToString(),
            state = completed.Installation.State.ToString(),
            completed.Installation.Version,
            actorId = completed.Installation.ActorId.Value,
            correlationId = completed.Installation.CorrelationId.Value,
            auditEventId = completed.AuditEvent.EventId,
            auditSequence = completed.AuditEvent.Sequence,
        });
        return 0;
    }
    catch (OperationCanceledException)
    {
        await Console.Error.WriteLineAsync("Setup was canceled.");
        return 130;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        await WriteJsonAsync(new
        {
            succeeded = false,
            failure = new
            {
                code = FailureCode.RecoverableExternalFailure.ToString(),
                message = "Setup storage could not be initialized or updated.",
                isRetryable = true,
            },
        });
        return 1;
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}

static async Task<int> ConfigureAgentAsync(string[] arguments, bool create)
{
    if (!TryParseAgentOptions(arguments, out var options, out var error))
    {
        await Console.Error.WriteLineAsync(error);
        return 1;
    }

    string dataDirectory;
    try
    {
        dataDirectory = Path.GetFullPath(options!.DataDirectory);
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
    {
        await Console.Error.WriteLineAsync("--data-directory is not a valid filesystem path.");
        return 1;
    }

    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentForge:Installation:DataDirectory"] = dataDirectory,
        })
        .Build();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddAgentForgeSetup(configuration);
    services.AddAgentForgePersistence(configuration);
    services.AddAgentForgeSecurity(configuration);
    services.AddAgentForgeAudit();

    await using var provider = services.BuildServiceProvider(validateScopes: true);
    await using var scope = provider.CreateAsyncScope();
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;
    try
    {
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync(cancellation.Token);
        var candidate = new AgentIdentityCandidate(
            options.Name,
            options.Expertise,
            options.Mission,
            options.Language,
            options.TimeZone,
            options.Style,
            options.Workspace,
            new AgentModelPolicy(options.ProviderId, options.DataLocality, options.AllowFallback),
            new AgentMemoryPolicy(options.MemoryScope, options.MemoryRetentionDays),
            new AgentCapabilityPolicy(options.NetworkPosture, [], []),
            new AgentBudget(
                options.MaxTurns,
                options.MaxToolInvocations,
                options.MaxInputTokens,
                options.MaxOutputTokens,
                options.MaxWallClockSeconds),
            new ChildAgentLimits(
                options.MaxChildDepth,
                options.MaxChildren,
                options.MaxChildConcurrency,
                options.MaxChildTotalTokens),
            new AgentLearningPolicy(options.LearningMode, options.MutableSkillScope));
        var setup = scope.ServiceProvider.GetRequiredService<ISetupApplicationService>();
        if (!create)
        {
            var preview = await setup.PreviewAgentAsync(new PreviewAgentRequest(
                candidate,
                new ActorId(options.ActorId),
                new CorrelationId(options.CorrelationId)), cancellation.Token);
            if (!preview.IsSuccess)
            {
                return await WriteFailureAsync(preview.Failure!);
            }

            await WriteEffectiveAgentAsync(preview.Value, agentId: null, created: false);
            return 0;
        }

        var created = await setup.CreateAgentAsync(new CreateAgentRequest(
            candidate,
            new ActorId(options.ActorId),
            new CorrelationId(options.CorrelationId)), cancellation.Token);
        if (!created.IsSuccess)
        {
            return await WriteFailureAsync(created.Failure!);
        }

        await WriteEffectiveAgentAsync(
            created.Value.EffectiveDefinition,
            created.Value.Agent.Id.ToString(),
            created: true);
        return 0;
    }
    catch (OperationCanceledException)
    {
        await Console.Error.WriteLineAsync("Agent setup was canceled.");
        return 130;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        return await WriteFailureAsync(new DomainFailure(
            FailureCode.RecoverableExternalFailure,
            "Agent setup storage could not be initialized or updated.",
            IsRetryable: true));
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}

static async Task<int> CompleteSetupAsync(string[] arguments)
{
    if (!TryParseCompleteOptions(arguments, out var options, out var error))
    {
        await Console.Error.WriteLineAsync(error);
        return 1;
    }

    string dataDirectory;
    try
    {
        dataDirectory = Path.GetFullPath(options!.DataDirectory);
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
    {
        await Console.Error.WriteLineAsync("--data-directory is not a valid filesystem path.");
        return 1;
    }

    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentForge:Installation:DataDirectory"] = dataDirectory,
        })
        .Build();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddAgentForgeSetup(configuration);
    services.AddAgentForgePersistence(configuration);
    services.AddAgentForgeSecurity(configuration);
    services.AddAgentForgeAudit();

    await using var provider = services.BuildServiceProvider(validateScopes: true);
    await using var scope = provider.CreateAsyncScope();
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;
    try
    {
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync(cancellation.Token);
        var result = await scope.ServiceProvider.GetRequiredService<ISetupApplicationService>()
            .CompleteAsync(new AgentForge.Domain.Security.CompleteSetupRequest(
                new ActorId(options.ActorId),
                new CorrelationId(options.CorrelationId)), cancellation.Token);
        if (!result.IsSuccess)
        {
            return await WriteFailureAsync(result.Failure!);
        }

        await WriteJsonAsync(new
        {
            succeeded = true,
            state = result.Value.Installation.State.ToString(),
            version = result.Value.Installation.Version,
            administratorId = result.Value.Administrator.Id.ToString(),
            actorId = result.Value.Administrator.ActorId.Value,
            credentialReference = new
            {
                store = result.Value.Administrator.ClientCredentialReference.Store,
                key = result.Value.Administrator.ClientCredentialReference.Key,
            },
            checks = result.Value.Checks,
        });
        return 0;
    }
    catch (OperationCanceledException)
    {
        await Console.Error.WriteLineAsync("Setup completion was canceled.");
        return 130;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        return await WriteFailureAsync(new DomainFailure(
            FailureCode.RecoverableExternalFailure,
            "Setup completion storage could not be initialized or updated.",
            IsRetryable: true));
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}

static async Task<int> WriteFailureAsync(DomainFailure failure)
{
    await WriteJsonAsync(new
    {
        succeeded = false,
        failure = new
        {
            code = failure.Code.ToString(),
            failure.Message,
            failure.IsRetryable,
        },
    });
    return failure.Code is FailureCode.ConcurrencyConflict ? 3 : 1;
}

static Task WriteEffectiveAgentAsync(
    EffectiveAgentDefinition definition,
    string? agentId,
    bool created) => WriteJsonAsync(new
    {
        succeeded = true,
        created,
        agentId,
        name = definition.Agent.Name,
        provider = definition.ProviderName,
        definition.Model,
        dataLocality = definition.Agent.ModelPolicy.DataLocality.ToString(),
        memoryScope = definition.Agent.MemoryPolicy.Scope.ToString(),
        networkPosture = definition.Agent.CapabilityPolicy.NetworkPosture.ToString(),
        learningMode = definition.Agent.LearningPolicy.Mode.ToString(),
        budget = definition.Agent.Budget,
        childLimits = definition.Agent.ChildLimits,
        capabilities = definition.Capabilities.Select(item => new
        {
            id = item.CapabilityId,
            decision = item.Decision.ToString(),
            item.Reason,
        }),
    });

static async Task<string[]> ReadInteractiveArgumentsAsync()
{
    static async Task<string> PromptAsync(string prompt)
    {
        await Console.Error.WriteAsync(prompt);
        return await Console.In.ReadLineAsync() ?? string.Empty;
    }

    var dataDirectory = await PromptAsync("Data directory: ");
    var actor = await PromptAsync("Operator actor ID: ");
    var correlation = await PromptAsync("Correlation ID: ");
    var installationId = await PromptAsync("Installation ID (optional GUID): ");
    var collected = new List<string>
    {
        "--data-directory", dataDirectory,
        "--actor", actor,
        "--correlation", correlation,
    };
    if (!string.IsNullOrWhiteSpace(installationId))
    {
        collected.Add("--installation-id");
        collected.Add(installationId);
    }

    return [.. collected];
}

static bool TryParseBeginOptions(
    string[] arguments,
    out SetupBeginOptions? options,
    out string? error)
{
    options = null;
    error = null;
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "--actor",
        "--correlation",
        "--data-directory",
        "--installation-id",
    };

    for (var index = 0; index < arguments.Length; index += 2)
    {
        if (index + 1 >= arguments.Length)
        {
            error = $"Option '{arguments[index]}' requires a value.";
            return false;
        }

        var name = arguments[index];
        if (!allowed.Contains(name))
        {
            error = $"Unknown setup option '{name}'.";
            return false;
        }

        if (!values.TryAdd(name, arguments[index + 1]))
        {
            error = $"Option '{name}' may be specified only once.";
            return false;
        }
    }

    if (!Require(values, "--data-directory", out var dataDirectory, out error) ||
        !Require(values, "--actor", out var actorId, out error) ||
        !Require(values, "--correlation", out var correlationId, out error))
    {
        return false;
    }

    InstallationId? installationId = null;
    if (values.TryGetValue("--installation-id", out var requestedId))
    {
        if (!Guid.TryParseExact(requestedId, "D", out var parsedId) || parsedId == Guid.Empty)
        {
            error = "--installation-id must be a non-empty GUID in D format.";
            return false;
        }

        installationId = new InstallationId(parsedId);
    }

    options = new SetupBeginOptions(dataDirectory, actorId, correlationId, installationId);
    return true;
}

static bool TryParseAgentOptions(
    string[] arguments,
    out SetupAgentOptions? options,
    out string? error)
{
    options = null;
    error = null;
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "--actor", "--allow-fallback", "--correlation", "--data-directory", "--data-locality",
        "--expertise", "--language", "--learning-mode", "--max-child-concurrency", "--max-child-depth",
        "--max-child-tokens", "--max-children", "--max-input-tokens", "--max-output-tokens",
        "--max-tool-invocations", "--max-turns", "--max-wall-seconds", "--memory-retention-days",
        "--memory-scope", "--mission", "--mutable-skill-scope", "--name", "--network-posture",
        "--provider-id", "--style", "--timezone", "--workspace",
    };
    for (var index = 0; index < arguments.Length; index += 2)
    {
        if (index + 1 >= arguments.Length)
        {
            error = $"Option '{arguments[index]}' requires a value.";
            return false;
        }

        var name = arguments[index];
        if (!allowed.Contains(name))
        {
            error = $"Unknown agent option '{name}'.";
            return false;
        }

        if (!values.TryAdd(name, arguments[index + 1]))
        {
            error = $"Option '{name}' may be specified only once.";
            return false;
        }
    }

    if (!Require(values, "--data-directory", out var dataDirectory, out error) ||
        !Require(values, "--name", out var agentName, out error) ||
        !Require(values, "--provider-id", out var providerIdText, out error) ||
        !Require(values, "--actor", out var actorId, out error) ||
        !Require(values, "--correlation", out var correlationId, out error))
    {
        return false;
    }

    if (!Guid.TryParseExact(providerIdText, "D", out var providerId) || providerId == Guid.Empty)
    {
        error = "--provider-id must be a non-empty GUID in D format.";
        return false;
    }

    if (!TryEnum(values, "--data-locality", ModelDataLocality.LocalOnly, out var dataLocality, out error) ||
        !TryEnum(values, "--memory-scope", AgentMemoryScope.Agent, out var memoryScope, out error) ||
        !TryEnum(values, "--network-posture", NetworkPosture.Denied, out var networkPosture, out error) ||
        !TryEnum(values, "--learning-mode", LearningMode.Propose, out var learningMode, out error) ||
        !TryInt(values, "--memory-retention-days", memoryScope is AgentMemoryScope.Task ? 0 : 30, out var retentionDays, out error) ||
        !TryInt(values, "--max-turns", 64, out var maxTurns, out error) ||
        !TryInt(values, "--max-tool-invocations", 0, out var maxTools, out error) ||
        !TryLong(values, "--max-input-tokens", 16_000, out var maxInputTokens, out error) ||
        !TryLong(values, "--max-output-tokens", 4_000, out var maxOutputTokens, out error) ||
        !TryInt(values, "--max-wall-seconds", 3600, out var maxWallSeconds, out error) ||
        !TryInt(values, "--max-child-depth", 0, out var maxChildDepth, out error) ||
        !TryInt(values, "--max-children", 0, out var maxChildren, out error) ||
        !TryInt(values, "--max-child-concurrency", 0, out var maxChildConcurrency, out error) ||
        !TryLong(values, "--max-child-tokens", 0, out var maxChildTokens, out error))
    {
        return false;
    }

    var defaultMutableScope = learningMode switch
    {
        LearningMode.Off or LearningMode.Observe => MutableSkillScope.None,
        LearningMode.Propose => MutableSkillScope.ProposalWorkspaceOnly,
        LearningMode.ScopedAuto => MutableSkillScope.ApprovedSkillClasses,
        _ => MutableSkillScope.None,
    };
    if (!TryEnum(values, "--mutable-skill-scope", defaultMutableScope, out var mutableSkillScope, out error))
    {
        return false;
    }

    var allowFallback = false;
    if (values.TryGetValue("--allow-fallback", out var allowFallbackText) &&
        !bool.TryParse(allowFallbackText, out allowFallback))
    {
        error = "--allow-fallback must be true or false.";
        return false;
    }

    options = new SetupAgentOptions(
        dataDirectory,
        agentName,
        values.GetValueOrDefault("--expertise"),
        values.GetValueOrDefault("--mission"),
        values.GetValueOrDefault("--language", "en"),
        values.GetValueOrDefault("--timezone", "UTC"),
        values.GetValueOrDefault("--style", "Concise"),
        values.GetValueOrDefault("--workspace"),
        new ProviderProfileId(providerId),
        dataLocality,
        allowFallback,
        memoryScope,
        retentionDays,
        networkPosture,
        maxTurns,
        maxTools,
        maxInputTokens,
        maxOutputTokens,
        maxWallSeconds,
        maxChildDepth,
        maxChildren,
        maxChildConcurrency,
        maxChildTokens,
        learningMode,
        mutableSkillScope,
        actorId,
        correlationId);
    return true;
}

static bool TryParseCompleteOptions(
    string[] arguments,
    out SetupCompleteOptions? options,
    out string? error)
{
    options = null;
    error = null;
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var allowed = new HashSet<string>(["--actor", "--correlation", "--data-directory"], StringComparer.Ordinal);
    for (var index = 0; index < arguments.Length; index += 2)
    {
        if (index + 1 >= arguments.Length)
        {
            error = $"Option '{arguments[index]}' requires a value.";
            return false;
        }

        var name = arguments[index];
        if (!allowed.Contains(name))
        {
            error = $"Unknown setup completion option '{name}'.";
            return false;
        }

        if (!values.TryAdd(name, arguments[index + 1]))
        {
            error = $"Option '{name}' may be specified only once.";
            return false;
        }
    }

    if (!Require(values, "--data-directory", out var dataDirectory, out error) ||
        !Require(values, "--actor", out var actorId, out error) ||
        !Require(values, "--correlation", out var correlationId, out error))
    {
        return false;
    }

    options = new SetupCompleteOptions(dataDirectory, actorId, correlationId);
    return true;
}

static bool TryInt(
    IReadOnlyDictionary<string, string> values,
    string name,
    int defaultValue,
    out int value,
    out string? error)
{
    if (!values.TryGetValue(name, out var text))
    {
        value = defaultValue;
        error = null;
        return true;
    }

    if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value))
    {
        error = null;
        return true;
    }

    error = $"{name} must be a non-negative integer.";
    return false;
}

static bool TryLong(
    IReadOnlyDictionary<string, string> values,
    string name,
    long defaultValue,
    out long value,
    out string? error)
{
    if (!values.TryGetValue(name, out var text))
    {
        value = defaultValue;
        error = null;
        return true;
    }

    if (long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value))
    {
        error = null;
        return true;
    }

    error = $"{name} must be a non-negative integer.";
    return false;
}

static bool TryEnum<T>(
    IReadOnlyDictionary<string, string> values,
    string name,
    T defaultValue,
    out T value,
    out string? error)
    where T : struct, Enum
{
    if (!values.TryGetValue(name, out var text))
    {
        value = defaultValue;
        error = null;
        return true;
    }

    if (Enum.TryParse<T>(text, ignoreCase: true, out value) && Enum.IsDefined(value))
    {
        error = null;
        return true;
    }

    error = $"{name} must be one of: {string.Join(", ", Enum.GetNames<T>())}.";
    return false;
}

static bool Require(
    IReadOnlyDictionary<string, string> values,
    string name,
    out string value,
    out string? error)
{
    if (!values.TryGetValue(name, out value!) || string.IsNullOrWhiteSpace(value))
    {
        error = $"Required option '{name}' is missing or empty.";
        return false;
    }

    error = null;
    return true;
}

static Task WriteJsonAsync(object value) => Console.Out.WriteLineAsync(JsonSerializer.Serialize(
    value,
    new JsonSerializerOptions(JsonSerializerDefaults.Web)));

static void PrintHelp()
{
    Console.WriteLine("AgentForge CLI");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  agentforge status");
    Console.WriteLine("  agentforge setup status");
    Console.WriteLine("  agentforge setup begin --data-directory <path> --actor <id> --correlation <id> [--installation-id <guid>]");
    Console.WriteLine("  agentforge setup begin --interactive");
    Console.WriteLine("  agentforge setup agent preview --data-directory <path> --name <name> --provider-id <guid> --actor <id> --correlation <id> [policy options]");
    Console.WriteLine("  agentforge setup agent create --data-directory <path> --name <name> --provider-id <guid> --actor <id> --correlation <id> [policy options]");
    Console.WriteLine("  agentforge setup complete --data-directory <path> --actor <id> --correlation <id>");
}

internal sealed record SetupBeginOptions(
    string DataDirectory,
    string ActorId,
    string CorrelationId,
    InstallationId? InstallationId);

internal sealed record SetupAgentOptions(
    string DataDirectory,
    string Name,
    string? Expertise,
    string? Mission,
    string Language,
    string TimeZone,
    string Style,
    string? Workspace,
    ProviderProfileId ProviderId,
    ModelDataLocality DataLocality,
    bool AllowFallback,
    AgentMemoryScope MemoryScope,
    int MemoryRetentionDays,
    NetworkPosture NetworkPosture,
    int MaxTurns,
    int MaxToolInvocations,
    long MaxInputTokens,
    long MaxOutputTokens,
    int MaxWallClockSeconds,
    int MaxChildDepth,
    int MaxChildren,
    int MaxChildConcurrency,
    long MaxChildTotalTokens,
    LearningMode LearningMode,
    MutableSkillScope MutableSkillScope,
    string ActorId,
    string CorrelationId);

internal sealed record SetupCompleteOptions(
    string DataDirectory,
    string ActorId,
    string CorrelationId);
