using System.Net;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    PrintHelp();
    return 0;
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

static void PrintHelp()
{
    Console.WriteLine("AgentForge CLI");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  agentforge status");
    Console.WriteLine("  agentforge setup status");
}
