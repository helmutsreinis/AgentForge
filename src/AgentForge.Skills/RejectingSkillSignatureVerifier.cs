using AgentForge.Abstractions.Skills;
using AgentForge.Domain.Primitives;

namespace AgentForge.Skills;

internal sealed class RejectingSkillSignatureVerifier : ISkillSignatureVerifier
{
    public DomainResult<bool> Verify(SkillSignatureVerificationRequest request) =>
        DomainResult.Fail<bool>(new DomainFailure(
            FailureCode.UnsupportedCapability,
            "No trusted skill-signature verifier is configured."));
}
