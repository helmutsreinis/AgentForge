using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Mcp;
using AgentForge.Abstractions.Security;

namespace AgentForge.Host.Http;

internal sealed class McpAuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IInstallationStateReader installations,
        ILocalAdministratorAuthenticator authenticator)
    {
        if (!context.Request.Path.StartsWithSegments("/mcp"))
        {
            await next(context);
            return;
        }

        var result = await ApiAuthentication.AuthenticateAsync(
            context, installations, authenticator, context.RequestAborted);
        if (!result.Succeeded)
        {
            await result.Failure!.ExecuteAsync(context);
            return;
        }

        context.Items[McpCallerContextItems.Actor] = result.Actor!.Value;
        await next(context);
    }
}
