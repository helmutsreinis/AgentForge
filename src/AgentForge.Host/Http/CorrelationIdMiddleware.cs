using System.Diagnostics;

namespace AgentForge.Host.Http;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";
    private const int MaximumLength = 128;

    public async Task InvokeAsync(HttpContext httpContext, CorrelationContext correlationContext)
    {
        var supplied = httpContext.Request.Headers[HeaderName].FirstOrDefault();
        var generated = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");

        if (supplied is not null && !IsValid(supplied))
        {
            correlationContext.Set(generated);
            httpContext.TraceIdentifier = generated;
            httpContext.Response.Headers[HeaderName] = generated;
            await Results.Problem(
                type: "urn:agentforge:problem:invalid-correlation-id",
                title: "Invalid correlation identifier",
                detail: $"{HeaderName} may contain only ASCII letters, digits, '.', '_', '-', or ':' and must be at most {MaximumLength} characters.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["correlationId"] = generated })
                .ExecuteAsync(httpContext);
            correlationContext.Set(null);
            return;
        }

        var correlationId = supplied ?? generated;
        correlationContext.Set(correlationId);
        httpContext.TraceIdentifier = correlationId;
        httpContext.Response.OnStarting(() =>
        {
            httpContext.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        try
        {
            await next(httpContext);
        }
        finally
        {
            correlationContext.Set(null);
        }
    }

    private static bool IsValid(string value) =>
        value.Length is > 0 and <= MaximumLength && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');
}
