using AgentForge.Abstractions.Setup;
using AgentForge.Abstractions.Skills;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Setup;
using AgentForge.Domain.Skills;
using AgentForge.Skills;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.UnitTests;

public sealed class SkillRecoveryConfigurationInspectorTests
{
    [Fact]
    public async Task Recovery_inspection_validates_empty_registry_without_executing_skills()
    {
        await using var provider = Build(new FakeSkillRegistryRepository(throwOnList: false));
        await using var scope = provider.CreateAsyncScope();

        var check = await scope.ServiceProvider.GetRequiredService<IRecoveryConfigurationInspector>()
            .InspectAsync(new InstallationId(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(DoctorCheckStatus.Pass, check.Status);
        Assert.Contains("0 immutable", check.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recovery_inspection_fails_closed_when_durable_skill_state_cannot_be_reconstructed()
    {
        await using var provider = Build(new FakeSkillRegistryRepository(throwOnList: true));
        await using var scope = provider.CreateAsyncScope();

        var check = await scope.ServiceProvider.GetRequiredService<IRecoveryConfigurationInspector>()
            .InspectAsync(new InstallationId(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(DoctorCheckStatus.Fail, check.Status);
    }

    private static ServiceProvider Build(ISkillRegistryRepository repository)
    {
        var services = new ServiceCollection();
        services.AddSingleton(repository);
        services.AddAgentForgeSkills();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private sealed class FakeSkillRegistryRepository(bool throwOnList) : ISkillRegistryRepository
    {
        public ValueTask AddAsync(RegisteredSkillVersion version, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask UpdateAsync(
            RegisteredSkillVersion version,
            long expectedRecordVersion,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<RegisteredSkillVersion?> FindAsync(
            InstallationId installationId,
            SkillId skillId,
            SkillVersion version,
            CancellationToken cancellationToken) => ValueTask.FromResult<RegisteredSkillVersion?>(null);

        public ValueTask<RegisteredSkillVersion?> FindActiveAsync(
            InstallationId installationId,
            SkillId skillId,
            CancellationToken cancellationToken) => ValueTask.FromResult<RegisteredSkillVersion?>(null);

        public ValueTask<IReadOnlyList<RegisteredSkillVersion>> ListAsync(
            InstallationId installationId,
            CancellationToken cancellationToken) => throwOnList
                ? throw new InvalidOperationException("corrupt fixture")
                : ValueTask.FromResult<IReadOnlyList<RegisteredSkillVersion>>([]);
    }
}
