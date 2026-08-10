using AgentForge.Abstractions.Agents;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;

namespace AgentForge.Setup;

internal sealed class ConservativeAgentDefinitionEvaluator : IAgentDefinitionEvaluator
{
    private const int MaximumGrantCount = 64;

    public DomainResult<AgentIdentityCandidate> NormalizeAndValidate(AgentIdentityCandidate candidate)
    {
        if (candidate is null || candidate.ModelPolicy is null || candidate.MemoryPolicy is null ||
            candidate.CapabilityPolicy is null || candidate.Budget is null || candidate.ChildLimits is null ||
            candidate.LearningPolicy is null)
        {
            return Invalid<AgentIdentityCandidate>("Agent definition fields are required.");
        }

        var scalarFailure = ValidateScalars(candidate);
        if (scalarFailure is not null)
        {
            return DomainResult.Fail<AgentIdentityCandidate>(scalarFailure);
        }

        var budgetFailure = ValidateBounds(candidate);
        if (budgetFailure is not null)
        {
            return DomainResult.Fail<AgentIdentityCandidate>(budgetFailure);
        }

        var toolGrants = NormalizeGrants(candidate.CapabilityPolicy.ToolGrants, "tool");
        if (!toolGrants.IsSuccess)
        {
            return DomainResult.Fail<AgentIdentityCandidate>(toolGrants.Failure!);
        }

        var skillGrants = NormalizeGrants(candidate.CapabilityPolicy.SkillGrants, "skill");
        if (!skillGrants.IsSuccess)
        {
            return DomainResult.Fail<AgentIdentityCandidate>(skillGrants.Failure!);
        }

        string? workspace = null;
        if (!string.IsNullOrWhiteSpace(candidate.DefaultWorkspace))
        {
            try
            {
                if (!Path.IsPathFullyQualified(candidate.DefaultWorkspace))
                {
                    return Invalid<AgentIdentityCandidate>("Default workspace must be a fully qualified path.");
                }

                workspace = Path.GetFullPath(candidate.DefaultWorkspace);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Invalid<AgentIdentityCandidate>("Default workspace is not a valid filesystem path.");
            }
        }

        var normalized = candidate with
        {
            Name = candidate.Name.Trim(),
            Expertise = NormalizeOptional(candidate.Expertise),
            Mission = NormalizeOptional(candidate.Mission),
            PreferredLanguage = candidate.PreferredLanguage.Trim(),
            TimeZone = candidate.TimeZone.Trim(),
            ResponseStyle = candidate.ResponseStyle.Trim(),
            DefaultWorkspace = workspace,
            CapabilityPolicy = candidate.CapabilityPolicy with
            {
                ToolGrants = toolGrants.Value,
                SkillGrants = skillGrants.Value,
            },
        };
        return DomainResult.Success(normalized);
    }

    public DomainResult<EffectiveAgentDefinition> Evaluate(
        AgentIdentityCandidate normalizedCandidate,
        ProviderProfile providerProfile)
    {
        ArgumentNullException.ThrowIfNull(normalizedCandidate);
        ArgumentNullException.ThrowIfNull(providerProfile);

        if (providerProfile.Id != normalizedCandidate.ModelPolicy.PrimaryProviderProfileId)
        {
            return Invalid<EffectiveAgentDefinition>("The selected provider does not match the agent model policy.");
        }

        if (!providerProfile.Capabilities.TextGeneration)
        {
            return DomainResult.Fail<EffectiveAgentDefinition>(new DomainFailure(
                FailureCode.UnsupportedCapability,
                "The selected provider does not have observed text-generation capability."));
        }

        if (normalizedCandidate.ModelPolicy.DataLocality is ModelDataLocality.LocalOnly &&
            !IsLoopback(providerProfile.Endpoint))
        {
            return DomainResult.Fail<EffectiveAgentDefinition>(new DomainFailure(
                FailureCode.PolicyDenied,
                "A LocalOnly agent requires a loopback provider endpoint during bootstrap."));
        }

        var capabilities = new List<EffectiveCapability>
        {
            Allow("model.text", "The selected provider has observed text-generation capability."),
            Decide("model.streaming", providerProfile.Capabilities.Streaming, "The selected provider capability probe controls streaming."),
            Decide("model.images", providerProfile.Capabilities.Images, "The selected provider capability probe controls image input."),
            new(
                "model.tool-calls",
                providerProfile.Capabilities.ToolCalls && normalizedCandidate.CapabilityPolicy.ToolGrants.Count > 0
                    ? CapabilityDecision.RequireApproval
                    : CapabilityDecision.Deny,
                "Tool calls require both provider support, an exact grant, and runtime approval."),
            new(
                "network.loopback",
                normalizedCandidate.CapabilityPolicy.NetworkPosture is NetworkPosture.LoopbackOnly
                    ? CapabilityDecision.Allow
                    : CapabilityDecision.Deny,
                "The configured network posture is applied exactly."),
            Deny("network.external", "No external network grant exists in the bootstrap policy."),
            Deny("credentials.materialize", "Agents cannot directly materialize provider credentials."),
            new(
                "agent.children",
                normalizedCandidate.ChildLimits.MaxChildren > 0
                    ? CapabilityDecision.Allow
                    : CapabilityDecision.Deny,
                "Child execution is bounded by the configured depth, count, concurrency, and token limits."),
            new(
                "learning.observe",
                normalizedCandidate.LearningPolicy.Mode is LearningMode.Off
                    ? CapabilityDecision.Deny
                    : CapabilityDecision.Allow,
                "Learning evidence capture follows the configured learning mode."),
            new(
                "learning.propose",
                normalizedCandidate.LearningPolicy.Mode is LearningMode.Propose or LearningMode.ScopedAuto
                    ? CapabilityDecision.Allow
                    : CapabilityDecision.Deny,
                "Only proposal creation is available before governed promotion is implemented."),
            new(
                "learning.promote",
                normalizedCandidate.LearningPolicy.Mode is LearningMode.ScopedAuto
                    ? CapabilityDecision.RequireApproval
                    : CapabilityDecision.Deny,
                "Bootstrap never grants autonomous promotion authority."),
            Deny("external.message", "No messaging policy is configured."),
            Deny("device.write", "No physical-control policy is configured."),
            Deny("privileged.execute", "No privileged-execution policy is configured."),
        };

        capabilities.AddRange(normalizedCandidate.CapabilityPolicy.ToolGrants.Select(
            grant => new EffectiveCapability(grant, CapabilityDecision.RequireApproval, "Exact tool grants remain approval-gated.")));
        capabilities.AddRange(normalizedCandidate.CapabilityPolicy.SkillGrants.Select(
            grant => new EffectiveCapability(grant, CapabilityDecision.RequireApproval, "Exact skill grants require catalog evidence and approval.")));

        return DomainResult.Success(new EffectiveAgentDefinition(
            normalizedCandidate,
            providerProfile.Name,
            providerProfile.Model,
            providerProfile.Capabilities,
            capabilities.OrderBy(item => item.CapabilityId, StringComparer.Ordinal).ToArray()));
    }

    private static DomainFailure? ValidateScalars(AgentIdentityCandidate candidate)
    {
        if (!IsBoundedText(candidate.Name, 128, required: true) ||
            !IsBoundedText(candidate.Expertise, 512, required: false) ||
            !IsBoundedText(candidate.Mission, 4096, required: false) ||
            !IsBoundedText(candidate.PreferredLanguage, 35, required: true) ||
            !IsBoundedText(candidate.TimeZone, 128, required: true) ||
            !IsBoundedText(candidate.ResponseStyle, 512, required: true) ||
            !IsBoundedText(candidate.DefaultWorkspace, 1024, required: false) ||
            candidate.ModelPolicy.PrimaryProviderProfileId.Value == Guid.Empty)
        {
            return new DomainFailure(FailureCode.ValidationFailure, "Agent identity contains missing, oversized, or control-character data.");
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(candidate.TimeZone.Trim());
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return new DomainFailure(FailureCode.ValidationFailure, "Agent timezone is not recognized on this host.");
        }

        return null;
    }

    private static DomainFailure? ValidateBounds(AgentIdentityCandidate candidate)
    {
        var budget = candidate.Budget;
        if (budget.MaxTurns is < 1 or > 1000 ||
            budget.MaxToolInvocations is < 0 or > 10000 ||
            budget.MaxInputTokens is < 1 or > 100_000_000 ||
            budget.MaxOutputTokens is < 1 or > 100_000_000 ||
            budget.MaxWallClockSeconds is < 1 or > 86400)
        {
            return new DomainFailure(FailureCode.ValidationFailure, "Agent budget is outside bootstrap safety bounds.");
        }

        if (candidate.MemoryPolicy.RetentionDays is < 0 or > 3650 ||
            candidate.MemoryPolicy.Scope is AgentMemoryScope.Task && candidate.MemoryPolicy.RetentionDays != 0)
        {
            return new DomainFailure(FailureCode.ValidationFailure, "Memory retention is inconsistent with the selected scope.");
        }

        var children = candidate.ChildLimits;
        var totalBudget = budget.MaxInputTokens + budget.MaxOutputTokens;
        var disabledChildrenAreNonZero = children.MaxChildren == 0 &&
            (children.MaxDepth != 0 || children.MaxConcurrency != 0 || children.MaxTotalTokens != 0);
        if (children.MaxDepth is < 0 or > 16 ||
            children.MaxChildren is < 0 or > 100 ||
            children.MaxConcurrency is < 0 or > 32 ||
            children.MaxTotalTokens < 0 ||
            disabledChildrenAreNonZero ||
            children.MaxChildren > 0 && (children.MaxDepth == 0 || children.MaxConcurrency == 0 ||
                children.MaxConcurrency > children.MaxChildren || children.MaxTotalTokens == 0 ||
                children.MaxTotalTokens > totalBudget))
        {
            return new DomainFailure(FailureCode.PolicyDenied, "Child-agent limits cannot exceed the parent bootstrap budget or recursion bounds.");
        }

        var learningIsConsistent = candidate.LearningPolicy.Mode switch
        {
            LearningMode.Off or LearningMode.Observe => candidate.LearningPolicy.MutableSkillScope is MutableSkillScope.None,
            LearningMode.Propose => candidate.LearningPolicy.MutableSkillScope is MutableSkillScope.ProposalWorkspaceOnly,
            LearningMode.ScopedAuto => candidate.LearningPolicy.MutableSkillScope is MutableSkillScope.ApprovedSkillClasses,
            _ => false,
        };
        if (!learningIsConsistent)
        {
            return new DomainFailure(FailureCode.PolicyDenied, "Learning mode and mutable-skill scope are inconsistent.");
        }

        if (candidate.ModelPolicy.DataLocality is ModelDataLocality.LocalOnly && candidate.ModelPolicy.AllowFallback)
        {
            return new DomainFailure(FailureCode.PolicyDenied, "LocalOnly model routing cannot enable fallback during bootstrap.");
        }

        return null;
    }

    private static DomainResult<IReadOnlyList<string>> NormalizeGrants(IReadOnlyList<string> grants, string prefix)
    {
        if (grants is null || grants.Count > MaximumGrantCount)
        {
            return Invalid<IReadOnlyList<string>>($"At most {MaximumGrantCount} exact {prefix} grants are allowed.");
        }

        var normalized = grants
            .Select(item => item?.Trim() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Any(item => item.Length is < 3 or > 256 ||
            !item.StartsWith(prefix + ":", StringComparison.Ordinal) ||
            item.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is ':' or '.' or '-' or '_' or '/'))))
        {
            return Invalid<IReadOnlyList<string>>($"Every {prefix} grant must be an exact, bounded identifier prefixed with '{prefix}:'.");
        }

        return DomainResult.Success<IReadOnlyList<string>>(normalized);
    }

    private static bool IsBoundedText(string? value, int maximumLength, bool required) =>
        (!required && string.IsNullOrWhiteSpace(value)) ||
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsLoopback(Uri endpoint) =>
        endpoint.IsLoopback ||
        string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    private static EffectiveCapability Allow(string id, string reason) => new(id, CapabilityDecision.Allow, reason);

    private static EffectiveCapability Deny(string id, string reason) => new(id, CapabilityDecision.Deny, reason);

    private static EffectiveCapability Decide(string id, bool allowed, string reason) =>
        new(id, allowed ? CapabilityDecision.Allow : CapabilityDecision.Deny, reason);

    private static DomainResult<T> Invalid<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ValidationFailure, message));
}
