using System.Security.Cryptography;
using System.Text.Json;
using AgentForge.Abstractions.Security;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Security;

namespace AgentForge.Security;

internal sealed class CapabilityPolicyEvaluator : ICapabilityPolicyEvaluator
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public CapabilityEvaluation Evaluate(
        AuthorizationContext context,
        CapabilityPolicySnapshot policy,
        CapabilityApproval? approval,
        DateTimeOffset evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.InstallationId != context.InstallationId ||
            policy.InstallationVersion != context.InstallationVersion || policy.AgentId != context.AgentId ||
            policy.AgentVersion != context.AgentVersion)
        {
            return Deny(context, "Policy scope, installation version, or agent version does not match the authorization request.");
        }

        var matches = policy.Rules.Where(item =>
            string.Equals(item.CapabilityId, context.CapabilityId, StringComparison.Ordinal) &&
            item.RiskClass == context.RiskClass).ToArray();
        if (matches.Length != 1)
        {
            return Deny(context, matches.Length == 0
                ? "Missing policy denies this capability."
                : "Ambiguous policy denies this capability.");
        }

        var rule = matches[0];
        if (!Enum.IsDefined(rule.Decision))
        {
            return Deny(context, "Invalid policy decision denies this capability.");
        }

        if (rule.Decision is CapabilityDecision.Allow)
        {
            return new CapabilityEvaluation(CapabilityDecision.Allow, rule.Reason, context.RequestHash);
        }

        if (rule.Decision is CapabilityDecision.Deny)
        {
            return Deny(context, rule.Reason);
        }

        if (approval is null || !Matches(approval, context) ||
            approval.State is not CapabilityApprovalState.Active || evaluatedAt >= approval.ExpiresAt)
        {
            return new CapabilityEvaluation(
                CapabilityDecision.RequireApproval,
                "An active exact approval is required.",
                context.RequestHash);
        }

        return approval.Disposition is CapabilityApprovalDisposition.Deny
            ? Deny(context, "An exact active denial applies to this request.", approval.Id)
            : new CapabilityEvaluation(
                CapabilityDecision.Allow,
                "An exact active grant applies to this request.",
                context.RequestHash,
                approval.Id);
    }

    public CapabilityPolicySnapshot Intersect(
        CapabilityPolicySnapshot parent,
        CapabilityPolicySnapshot child)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);
        var parentRules = parent.Rules
            .GroupBy(item => (item.CapabilityId, item.RiskClass))
            .ToDictionary(group => group.Key, group => group.Count() == 1 ? group.Single() : null);
        var childRules = child.Rules
            .GroupBy(item => (item.CapabilityId, item.RiskClass))
            .ToDictionary(group => group.Key, group => group.Count() == 1 ? group.Single() : null);
        var keys = parentRules.Keys.Union(childRules.Keys)
            .OrderBy(item => item.CapabilityId, StringComparer.Ordinal)
            .ThenBy(item => item.RiskClass)
            .ToArray();
        var sameInstallation = parent.InstallationId == child.InstallationId &&
            parent.InstallationVersion == child.InstallationVersion;
        var rules = keys.Select(key =>
        {
            parentRules.TryGetValue(key, out var parentRule);
            childRules.TryGetValue(key, out var childRule);
            var decision = !sameInstallation || parentRule is null || childRule is null
                ? CapabilityDecision.Deny
                : MostRestrictive(parentRule.Decision, childRule.Decision);
            return new CapabilityPolicyRule(
                key.CapabilityId,
                key.RiskClass,
                decision,
                decision is CapabilityDecision.Deny
                    ? "Parent/child policy intersection denied or omitted this capability."
                    : "Parent/child policy intersection selected the most restrictive decision.");
        }).ToArray();
        return new CapabilityPolicySnapshot(
            child.InstallationId,
            child.InstallationVersion,
            child.AgentId,
            child.AgentVersion,
            rules,
            ComputeFingerprint(
                child.InstallationId,
                child.InstallationVersion,
                child.AgentId,
                child.AgentVersion,
                rules));
    }

    private static bool Matches(CapabilityApproval approval, AuthorizationContext context) =>
        approval.InstallationId == context.InstallationId &&
        approval.InstallationVersion == context.InstallationVersion &&
        approval.AgentId == context.AgentId &&
        approval.AgentVersion == context.AgentVersion &&
        approval.RequestActorId == context.ActorId &&
        string.Equals(approval.CapabilityId, context.CapabilityId, StringComparison.Ordinal) &&
        approval.RiskClass == context.RiskClass &&
        string.Equals(approval.ToolId, context.ToolId, StringComparison.Ordinal) &&
        string.Equals(approval.ToolVersion, context.ToolVersion, StringComparison.Ordinal) &&
        string.Equals(approval.ToolDescriptorHash, context.ToolDescriptorHash, StringComparison.Ordinal) &&
        string.Equals(approval.ParametersHash, context.ParametersHash, StringComparison.Ordinal) &&
        approval.TargetKind == context.TargetKind &&
        string.Equals(approval.TargetHash, context.TargetHash, StringComparison.Ordinal) &&
        string.Equals(approval.WorkspaceHash, context.WorkspaceHash, StringComparison.Ordinal) &&
        string.Equals(approval.RequestHash, context.RequestHash, StringComparison.Ordinal);

    private static CapabilityDecision MostRestrictive(CapabilityDecision first, CapabilityDecision second)
    {
        if (!Enum.IsDefined(first) || !Enum.IsDefined(second) ||
            first is CapabilityDecision.Deny || second is CapabilityDecision.Deny)
        {
            return CapabilityDecision.Deny;
        }

        return first is CapabilityDecision.RequireApproval || second is CapabilityDecision.RequireApproval
            ? CapabilityDecision.RequireApproval
            : CapabilityDecision.Allow;
    }

    private static string ComputeFingerprint(
        AgentForge.Domain.Primitives.InstallationId installationId,
        long installationVersion,
        AgentForge.Domain.Agents.AgentIdentityId agentId,
        long agentVersion,
        IReadOnlyList<CapabilityPolicyRule> rules)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            InstallationId = installationId.ToString(),
            InstallationVersion = installationVersion,
            AgentId = agentId.ToString(),
            AgentVersion = agentVersion,
            Rules = rules,
        }, SerializerOptions);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    }

    private static CapabilityEvaluation Deny(
        AuthorizationContext context,
        string reason,
        CapabilityApprovalId? approvalId = null) =>
        new(CapabilityDecision.Deny, reason, context.RequestHash, approvalId);
}
