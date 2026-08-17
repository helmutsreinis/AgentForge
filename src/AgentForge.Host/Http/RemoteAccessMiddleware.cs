using Microsoft.Extensions.Options;

namespace AgentForge.Host.Http;

internal sealed class RemoteAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IOptions<HostSecurityOptions> options)
    {
        var address = context.Connection.RemoteIpAddress;
        var remote = address is not null && !System.Net.IPAddress.IsLoopback(address);
        if (remote && (!options.Value.RemoteEnabled || !context.Request.IsHttps))
        {
            await Problem(context, StatusCodes.Status403Forbidden,
                "Remote access denied", "Remote requests require explicitly enabled TLS mode.");
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        var requestOrigin = $"{context.Request.Scheme}://{context.Request.Host.Value}";
        var safeSameOriginRead = HttpMethods.IsGet(context.Request.Method) ||
            HttpMethods.IsHead(context.Request.Method);
        if (remote && (string.IsNullOrWhiteSpace(origin)
                ? !safeSameOriginRead || !options.Value.AllowedOrigins.Contains(requestOrigin, StringComparer.Ordinal)
                : !options.Value.AllowedOrigins.Contains(origin, StringComparer.Ordinal)))
        {
            await Problem(context, StatusCodes.Status403Forbidden,
                "Origin denied", "The remote browser origin is not explicitly allowed.");
            return;
        }

        if (context.Request.ContentLength > options.Value.MaximumRequestBodyBytes)
        {
            await Problem(context, StatusCodes.Status413PayloadTooLarge,
                "Request too large", "The request body exceeds the configured bound.");
            return;
        }

        await next(context);
    }

    private static Task Problem(HttpContext context, int status, string title, string detail) =>
        Results.Problem(
            type: "urn:agentforge:problem:remote-access",
            title: title,
            detail: detail,
            statusCode: status,
            extensions: new Dictionary<string, object?>
            {
                ["correlationId"] = context.TraceIdentifier,
            }).ExecuteAsync(context);
}
