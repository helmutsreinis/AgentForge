using AgentForge.Abstractions.HttpApi;
using AgentForge.Abstractions.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.HttpApi;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentForgeHttpApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(provider => HttpApiClient.Create(
            provider.GetRequiredService<AgentForge.Abstractions.Time.IClock>()));
        services.AddSingleton<IHttpApiConnectivityProbe, HttpApiConnectivityProbe>();
        services.AddScoped<IHttpApiConfigurationService, HttpApiConfigurationService>();
        services.AddScoped<IHttpApiReadService, HttpApiReadService>();
        services.AddScoped<IHttpApiRequestResolver, HttpApiRequestResolver>();
        services.AddScoped<IBuiltInToolHandler, HttpApiBuiltInToolHandler>();
        return services;
    }
}

internal sealed class HttpApiRequestResolver(IHttpApiProfileRepository profiles) : IHttpApiRequestResolver
{
    public async Task<AgentForge.Domain.Primitives.DomainResult<ResolvedHttpApiRequest>> ResolveAsync(
        AgentForge.Domain.Primitives.InstallationId installationId,
        AgentForge.Domain.HttpApi.HttpApiProfileId profileId,
        string relativePath,
        IReadOnlyDictionary<string, string> query,
        CancellationToken cancellationToken)
    {
        var profile = await profiles.FindAsync(installationId, profileId, cancellationToken);
        if (profile is null || !profile.IsEnabled)
        {
            return AgentForge.Domain.Primitives.DomainResult.Fail<ResolvedHttpApiRequest>(
                new AgentForge.Domain.Primitives.DomainFailure(
                    AgentForge.Domain.Primitives.FailureCode.UnsupportedCapability,
                    "The selected HTTP API profile is unavailable."));
        }
        var endpoint = HttpApiContract.BuildEndpoint(profile.BaseEndpoint, relativePath, query);
        return endpoint.IsSuccess
            ? AgentForge.Domain.Primitives.DomainResult.Success(new ResolvedHttpApiRequest(profile, endpoint.Value))
            : AgentForge.Domain.Primitives.DomainResult.Fail<ResolvedHttpApiRequest>(endpoint.Failure!);
    }
}
