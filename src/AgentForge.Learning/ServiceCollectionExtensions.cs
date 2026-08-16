using AgentForge.Abstractions.Learning;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Learning;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentForgeLearning(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<ILearningGovernanceService, LearningGovernanceService>();
        services.AddScoped<ILearningCandidateProposalService, LearningCandidateProposalService>();
        return services;
    }
}
