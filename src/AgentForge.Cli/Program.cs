using System.Net;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Setup;
using AgentForge.Audit;
using AgentForge.Domain.Primitives;
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
}

internal sealed record SetupBeginOptions(
    string DataDirectory,
    string ActorId,
    string CorrelationId,
    InstallationId? InstallationId);
