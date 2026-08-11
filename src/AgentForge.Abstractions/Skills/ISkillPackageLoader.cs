using AgentForge.Domain.Primitives;
using AgentForge.Domain.Skills;

namespace AgentForge.Abstractions.Skills;

public sealed record SkillSignatureVerificationRequest(
    string PackageHash,
    string Algorithm,
    string KeyId,
    string Signature);

public interface ISkillSignatureVerifier
{
    DomainResult<bool> Verify(SkillSignatureVerificationRequest request);
}

public interface ISkillPackageLoader
{
    Task<DomainResult<LoadedSkillPackage>> LoadAsync(
        string packageDirectory,
        CancellationToken cancellationToken);
}
