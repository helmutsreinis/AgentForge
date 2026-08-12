using AgentForge.Abstractions.Setup;
using AgentForge.Abstractions.Skills;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Setup;
using AgentForge.Domain.Skills;

namespace AgentForge.Skills;

internal sealed class SkillRecoveryConfigurationInspector(ISkillRegistryRepository repository)
    : IRecoveryConfigurationInspector
{
    public async Task<DoctorCheck> InspectAsync(
        InstallationId installationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var versions = await repository.ListAsync(installationId, cancellationToken);
            var valid = versions.All(SkillGovernanceStateMachine.IsValid) &&
                versions.Where(item => item.Status == SkillPackageStatus.Active)
                    .GroupBy(item => item.Package.Id)
                    .All(group => group.Count() == 1);
            return valid
                ? new DoctorCheck(
                    "skill.registry",
                    DoctorCheckStatus.Pass,
                    $"Validated {versions.Count} immutable governed skill version(s).")
                : Failure("Skill registry contains an invalid record or more than one active version.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            return Failure("Skill registry could not be reconstructed from durable state.");
        }
    }

    private static DoctorCheck Failure(string summary) =>
        new("skill.registry", DoctorCheckStatus.Fail, summary);
}
