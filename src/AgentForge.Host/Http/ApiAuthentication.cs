using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Security;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;

namespace AgentForge.Host.Http;

internal sealed record ApiAuthenticationResult(
    InstallationSnapshot? Installation,
    ActorId? Actor,
    IResult? Failure)
{
    public bool Succeeded => Failure is null;
}

internal static class ApiAuthentication
{
    internal static async Task<ApiAuthenticationResult> AuthenticateAsync(
        HttpContext context,
        IInstallationStateReader stateReader,
        ILocalAdministratorAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        var state = await stateReader.ReadAsync(cancellationToken);
        if (!state.IsReady)
            return Failed(StatusCodes.Status503ServiceUnavailable, "Setup required",
                "The production API is unavailable until setup is complete.", context, state);
        if (!TryGetBearerCredential(context.Request, out var credential))
            return Failed(StatusCodes.Status401Unauthorized, "Authentication required",
                "A valid local administrator bearer credential is required.", context, state);
        try
        {
            var authenticated = await authenticator.AuthenticateAsync(state.Id, credential, cancellationToken);
            return authenticated.IsSuccess
                ? new ApiAuthenticationResult(state, authenticated.Value, null)
                : Failed(StatusCodes.Status401Unauthorized, "Authentication failed",
                    "The supplied local administrator credential is invalid.", context, state);
        }
        finally
        {
            Array.Clear(credential);
        }
    }

    internal static bool TryGetBearerCredential(HttpRequest request, out char[] credential)
    {
        credential = [];
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var value = header.AsSpan(prefix.Length);
        if (value.Length is < 1 or > 256 || value.Contains(',') || value.Contains('\r') || value.Contains('\n'))
            return false;
        credential = value.ToArray();
        return true;
    }

    private static ApiAuthenticationResult Failed(
        int status, string title, string detail, HttpContext context, InstallationSnapshot state) => new(
        state,
        null,
        Results.Problem(
            type: status == StatusCodes.Status503ServiceUnavailable
                ? "urn:agentforge:problem:setup-required"
                : "urn:agentforge:problem:authentication-required",
            title: title,
            detail: detail,
            statusCode: status,
            extensions: new Dictionary<string, object?>
            {
                ["correlationId"] = context.TraceIdentifier,
            }));
}
