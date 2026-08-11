using System.Security.Cryptography;
using System.Text.Json;
using AgentForge.Abstractions.Security;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Security;

namespace AgentForge.Security;

internal sealed class CapabilityPolicyFactory : ICapabilityPolicyFactory
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public CapabilityPolicySnapshot Create(AgentIdentity agent, AuthorizationContext context)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(context);
        var isGranted = agent.CapabilityPolicy.ToolGrants.Contains(context.CapabilityId, StringComparer.Ordinal) ||
            agent.CapabilityPolicy.SkillGrants.Contains(context.CapabilityId, StringComparer.Ordinal);
        CapabilityPolicyRule[] rules = isGranted
            ? [new(
                context.CapabilityId,
                context.RiskClass,
                CapabilityDecision.RequireApproval,
                "Exact configured grants remain approval-gated.")]
            : [];
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            InstallationId = agent.InstallationId.ToString(),
            context.InstallationVersion,
            AgentId = agent.Id.ToString(),
            AgentVersion = agent.Version,
            Rules = rules,
        }, SerializerOptions);
        return new CapabilityPolicySnapshot(
            agent.InstallationId,
            context.InstallationVersion,
            agent.Id,
            agent.Version,
            rules,
            $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}");
    }
}
