using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Orchestration;

public readonly record struct ChildDelegationId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public enum DelegationRole
{
    Worker,
    Handoff,
    Manager,
    Reviewer,
}

public sealed record ParentDelegationAuthority(
    OrchestrationTaskId ParentTaskId,
    InstallationId InstallationId,
    AgentIdentityId ParentAgentId,
    long ParentAgentVersion,
    int CurrentDepth,
    int SpawnedChildren,
    int ActiveChildren,
    int MaximumDepth,
    int MaximumChildren,
    int MaximumConcurrency,
    IReadOnlyList<string> CapabilityIds,
    IReadOnlyList<string> ContextEvidenceHashes,
    TaskExecutionBudget RemainingBudget,
    TaskExecutionBudget PerChildBudget,
    string PolicySnapshotHash,
    string SkillSnapshotHash,
    DateTimeOffset ExpiresAt);

public sealed record ChildDelegationRequest(
    ChildDelegationId Id,
    AgentIdentityId ChildAgentId,
    long ChildAgentVersion,
    DelegationRole Role,
    IReadOnlyList<string> RequestedCapabilities,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> RequestedContextEvidenceHashes,
    TaskExecutionBudget RequestedBudget,
    string PurposeEvidenceHash,
    CorrelationId CorrelationId,
    CorrelationId? CausationId);

public sealed record ChildDelegationGrant(
    ChildDelegationId Id,
    OrchestrationTaskId ParentTaskId,
    InstallationId InstallationId,
    AgentIdentityId ParentAgentId,
    long ParentAgentVersion,
    AgentIdentityId ChildAgentId,
    long ChildAgentVersion,
    DelegationRole Role,
    int Depth,
    IReadOnlyList<string> CapabilityIds,
    IReadOnlyList<string> ContextEvidenceHashes,
    TaskExecutionBudget Budget,
    string PolicySnapshotHash,
    string SkillSnapshotHash,
    string PurposeEvidenceHash,
    string GrantHash,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    CorrelationId CorrelationId,
    CorrelationId? CausationId);

public static class DelegationAuthorityEvaluator
{
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    public static DomainResult<ChildDelegationGrant> Evaluate(
        ParentDelegationAuthority parent,
        ChildDelegationRequest request,
        DateTimeOffset issuedAt)
    {
        if (!IsValid(parent) || !IsValid(request) || issuedAt >= parent.ExpiresAt)
        {
            return Invalid("Delegation requires current bounded parent authority and child intent.");
        }

        if (parent.CurrentDepth >= parent.MaximumDepth ||
            parent.SpawnedChildren >= parent.MaximumChildren ||
            parent.ActiveChildren >= parent.MaximumConcurrency)
        {
            return Denied("Delegation depth, total child count, or active concurrency is exhausted.");
        }

        var parentCapabilities = parent.CapabilityIds.ToHashSet(StringComparer.Ordinal);
        var grantedCapabilities = request.RequestedCapabilities
            .Where(parentCapabilities.Contains)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (request.RequiredCapabilities.Any(capability =>
                !grantedCapabilities.Contains(capability, StringComparer.Ordinal)))
        {
            return Denied("A required child capability is not present in parent authority.");
        }

        var parentContext = parent.ContextEvidenceHashes.ToHashSet(StringComparer.Ordinal);
        if (request.RequestedContextEvidenceHashes.Any(hash => !parentContext.Contains(hash)))
        {
            return Denied("A child requested context outside the parent's explicit evidence set.");
        }

        var context = request.RequestedContextEvidenceHashes
            .Order(StringComparer.Ordinal)
            .ToArray();
        var budget = Intersect(request.RequestedBudget, parent.PerChildBudget, parent.RemainingBudget);
        if (budget.MaximumOutputTokens < 1 || budget.MaximumWallClockSeconds < 1)
        {
            return Denied("No positive child execution budget remains.");
        }

        var grant = new ChildDelegationGrant(
            request.Id,
            parent.ParentTaskId,
            parent.InstallationId,
            parent.ParentAgentId,
            parent.ParentAgentVersion,
            request.ChildAgentId,
            request.ChildAgentVersion,
            request.Role,
            parent.CurrentDepth + 1,
            grantedCapabilities,
            context,
            budget,
            parent.PolicySnapshotHash,
            parent.SkillSnapshotHash,
            request.PurposeEvidenceHash,
            OrchestrationTaskStateMachine.EmptyHash,
            issuedAt,
            parent.ExpiresAt,
            request.CorrelationId,
            request.CausationId);
        return DomainResult.Success(grant with { GrantHash = ComputeHash(grant) });
    }

    public static bool IsConsistent(ChildDelegationGrant? grant) =>
        grant is not null && grant.Id.Value != Guid.Empty && grant.ParentTaskId.Value != Guid.Empty &&
        grant.InstallationId.Value != Guid.Empty && grant.ParentAgentId.Value != Guid.Empty &&
        grant.ChildAgentId.Value != Guid.Empty && grant.ParentAgentVersion >= 0 && grant.ChildAgentVersion >= 0 &&
        grant.Depth is >= 1 and <= 16 && Enum.IsDefined(grant.Role) &&
        IsSortedDistinctBounded(grant.CapabilityIds, 256, 256) &&
        IsSortedDistinctHashes(grant.ContextEvidenceHashes, 256) && IsValid(grant.Budget) &&
        IsHash(grant.PolicySnapshotHash) && IsHash(grant.SkillSnapshotHash) &&
        IsHash(grant.PurposeEvidenceHash) && IsHash(grant.GrantHash) &&
        grant.ExpiresAt > grant.IssuedAt && IsBounded(grant.CorrelationId.Value, 128) &&
        (grant.CausationId is null || IsBounded(grant.CausationId.Value.Value, 128)) &&
        string.Equals(grant.GrantHash, ComputeHash(grant), StringComparison.Ordinal);

    private static TaskExecutionBudget Intersect(
        TaskExecutionBudget requested,
        TaskExecutionBudget perChild,
        TaskExecutionBudget remaining) => new(
        Math.Min(requested.MaximumToolCalls, Math.Min(perChild.MaximumToolCalls, remaining.MaximumToolCalls)),
        Math.Min(requested.MaximumInputTokens, Math.Min(perChild.MaximumInputTokens, remaining.MaximumInputTokens)),
        Math.Min(requested.MaximumOutputTokens, Math.Min(perChild.MaximumOutputTokens, remaining.MaximumOutputTokens)),
        Math.Min(requested.MaximumWallClockSeconds,
            Math.Min(perChild.MaximumWallClockSeconds, remaining.MaximumWallClockSeconds)));

    private static bool IsValid(ParentDelegationAuthority? parent) => parent is not null &&
        parent.ParentTaskId.Value != Guid.Empty && parent.InstallationId.Value != Guid.Empty &&
        parent.ParentAgentId.Value != Guid.Empty && parent.ParentAgentVersion >= 0 &&
        parent.CurrentDepth is >= 0 and <= 16 && parent.MaximumDepth is >= 0 and <= 16 &&
        parent.CurrentDepth <= parent.MaximumDepth && parent.SpawnedChildren is >= 0 and <= 256 &&
        parent.MaximumChildren is >= 0 and <= 256 && parent.SpawnedChildren <= parent.MaximumChildren &&
        parent.ActiveChildren is >= 0 and <= 128 && parent.MaximumConcurrency is >= 0 and <= 128 &&
        parent.ActiveChildren <= parent.MaximumConcurrency &&
        IsSortedDistinctBounded(parent.CapabilityIds, 256, 256, requireSorted: false) &&
        IsDistinctHashes(parent.ContextEvidenceHashes, 256) && IsValid(parent.RemainingBudget) &&
        IsValid(parent.PerChildBudget) && IsHash(parent.PolicySnapshotHash) &&
        IsHash(parent.SkillSnapshotHash);

    private static bool IsValid(ChildDelegationRequest? request) => request is not null &&
        request.Id.Value != Guid.Empty && request.ChildAgentId.Value != Guid.Empty &&
        request.ChildAgentVersion >= 0 && Enum.IsDefined(request.Role) &&
        IsSortedDistinctBounded(request.RequestedCapabilities, 256, 256, requireSorted: false) &&
        IsSortedDistinctBounded(request.RequiredCapabilities, 256, 256, requireSorted: false) &&
        request.RequiredCapabilities.All(required =>
            request.RequestedCapabilities.Contains(required, StringComparer.Ordinal)) &&
        IsDistinctHashes(request.RequestedContextEvidenceHashes, 256) && IsValid(request.RequestedBudget) &&
        IsHash(request.PurposeEvidenceHash) && IsBounded(request.CorrelationId.Value, 128) &&
        (request.CausationId is null || IsBounded(request.CausationId.Value.Value, 128));

    private static bool IsValid(TaskExecutionBudget? budget) => budget is not null &&
        budget.MaximumToolCalls is >= 0 and <= 1_024 &&
        budget.MaximumInputTokens is >= 0 and <= 10_000_000 &&
        budget.MaximumOutputTokens is >= 0 and <= 1_000_000 &&
        budget.MaximumWallClockSeconds is >= 0 and <= 86_400;

    private static bool IsSortedDistinctBounded(
        IReadOnlyList<string>? values,
        int maximumCount,
        int maximumLength,
        bool requireSorted = true) => values is not null && values.Count <= maximumCount &&
        values.All(value => IsBounded(value, maximumLength)) &&
        values.Distinct(StringComparer.Ordinal).Count() == values.Count &&
        (!requireSorted || values.SequenceEqual(values.Order(StringComparer.Ordinal), StringComparer.Ordinal));

    private static bool IsDistinctHashes(IReadOnlyList<string>? values, int maximumCount) =>
        values is not null && values.Count <= maximumCount && values.All(IsHash) &&
        values.Distinct(StringComparer.Ordinal).Count() == values.Count;

    private static bool IsSortedDistinctHashes(IReadOnlyList<string>? values, int maximumCount) =>
        IsDistinctHashes(values, maximumCount) &&
        values!.SequenceEqual(values!.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool IsHash(string? value) => value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept(LowerHex) < 0;

    private static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => !char.IsControl(character));

    private static string ComputeHash(ChildDelegationGrant grant)
    {
        var builder = new StringBuilder(2048);
        Append(builder, grant.Id);
        Append(builder, grant.ParentTaskId);
        Append(builder, grant.InstallationId);
        Append(builder, grant.ParentAgentId);
        Append(builder, grant.ParentAgentVersion);
        Append(builder, grant.ChildAgentId);
        Append(builder, grant.ChildAgentVersion);
        Append(builder, grant.Role);
        Append(builder, grant.Depth);
        foreach (var capability in grant.CapabilityIds)
        {
            Append(builder, capability);
        }

        foreach (var contextHash in grant.ContextEvidenceHashes)
        {
            Append(builder, contextHash);
        }

        Append(builder, grant.Budget.MaximumToolCalls);
        Append(builder, grant.Budget.MaximumInputTokens);
        Append(builder, grant.Budget.MaximumOutputTokens);
        Append(builder, grant.Budget.MaximumWallClockSeconds);
        Append(builder, grant.PolicySnapshotHash);
        Append(builder, grant.SkillSnapshotHash);
        Append(builder, grant.PurposeEvidenceHash);
        Append(builder, grant.IssuedAt.UtcTicks);
        Append(builder, grant.ExpiresAt.UtcTicks);
        Append(builder, grant.CorrelationId);
        Append(builder, grant.CausationId?.Value ?? string.Empty);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))}";
    }

    private static void Append(StringBuilder builder, object value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        builder.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append(';');
    }

    private static DomainResult<ChildDelegationGrant> Invalid(string message) =>
        DomainResult.Fail<ChildDelegationGrant>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<ChildDelegationGrant> Denied(string message) =>
        DomainResult.Fail<ChildDelegationGrant>(new DomainFailure(FailureCode.PolicyDenied, message));
}
