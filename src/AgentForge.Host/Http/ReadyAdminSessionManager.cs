using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Runtime;
using AgentForge.Domain.Scheduling;
using AgentForge.Domain.Search;
using AgentForge.Domain.Security;
using AgentForge.Domain.Skills;
using AgentForge.Domain.Tools;

namespace AgentForge.Host.Http;

internal sealed record ReadyAdminSession(
    string Hash,
    string CsrfToken,
    InstallationId InstallationId,
    ActorId ActorId,
    DateTimeOffset ExpiresAtUtc)
{
    public ConcurrentDictionary<string, ReadyAdminIdempotencyResult> Results { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, ReadyProviderEditPreview> ProviderPreviews { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, ReadyAgentEditPreview> AgentPreviews { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, ReadyProviderCreatePreview> ProviderCreatePreviews { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, ReadyAgentCreatePreview> AgentCreatePreviews { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, ReadyScheduleCreatePreview> ScheduleCreatePreviews { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, ReadyResearchPreview> ResearchPreviews { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, ReadyResearchReceipt> ResearchReceipts { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, BraveSearchConfigurationPreview> BraveSearchPreviews { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, ReadyAgentSkillGrantPreview> SkillGrantPreviews { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, ReadyAgentToolGrantPreview> ToolGrantPreviews { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, ReadyToolInvocationPreview> ToolInvocationPreviews { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, ReadyConversationToolPreview> ConversationToolPreviews { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, ReadyModelCatalogObservation> ModelCatalogObservations { get; } = new(StringComparer.Ordinal);

    public SemaphoreSlim MutationGate { get; } = new(1, 1);
}

internal sealed record ReadyAdminIdempotencyResult(string RequestHash, object Response);

internal sealed record ReadyModelCatalogObservation(
    ProviderProfileId ProviderId,
    long ProviderVersion,
    string Model,
    long? MaximumContextTokens,
    DateTimeOffset ObservedAtUtc);

internal sealed record ReadyProviderEditPreview(
    AgentIdentityId AgentId,
    ProviderProfileId ProviderId,
    long ExpectedInstallationVersion,
    long ExpectedProviderVersion,
    ProviderProfileCandidate Candidate,
    CorrelationId CorrelationId,
    string RequestHash);

internal sealed record ReadyAgentEditPreview(
    AgentIdentityId AgentId,
    long ExpectedInstallationVersion,
    long ExpectedAgentVersion,
    AgentIdentityCandidate Candidate,
    CorrelationId CorrelationId,
    string RequestHash);

internal sealed record ReadyProviderCreatePreview(
    long ExpectedInstallationVersion,
    ProviderProfileCandidate Candidate,
    bool UsesCredential,
    string CredentialFingerprint,
    CorrelationId CorrelationId,
    string RequestHash);

internal sealed record ReadyAgentCreatePreview(
    long ExpectedInstallationVersion,
    long ExpectedProviderVersion,
    AgentIdentityCandidate Candidate,
    CorrelationId CorrelationId,
    string RequestHash);

internal sealed record ReadyScheduleCreatePreview(
    ScheduleId ScheduleId,
    long ExpectedInstallationVersion,
    long ExpectedAgentVersion,
    long ExpectedProviderVersion,
    string RequestHash,
    CorrelationId CorrelationId);

internal sealed record ReadyResearchPreview(
    AgentIdentityId AgentId,
    long ExpectedAgentVersion,
    string Query,
    int MaximumResults,
    IReadOnlyList<string> ProviderIds,
    IReadOnlyDictionary<string, string> ProviderEvidenceHashes,
    string RequestHash,
    CorrelationId CorrelationId);

internal sealed record ReadyResearchReceipt(
    AgentIdentityId AgentId,
    string ReceiptHash,
    string QueryHash,
    IReadOnlyList<SearchCitation> Citations,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

internal sealed record ReadyAgentSkillGrantPreview(
    AgentIdentityId AgentId,
    long ExpectedInstallationVersion,
    long ExpectedAgentVersion,
    SkillId SkillId,
    SkillVersion? ActiveVersion,
    string? PackageHash,
    bool Grant,
    AgentIdentityCandidate Candidate,
    CorrelationId CorrelationId,
    string RequestHash);

internal sealed record ReadyAgentToolGrantPreview(
    AgentIdentityId AgentId,
    long ExpectedInstallationVersion,
    long ExpectedAgentVersion,
    string CapabilityId,
    bool Grant,
    AgentIdentityCandidate Candidate,
    CorrelationId CorrelationId,
    string RequestHash);

internal sealed record ReadyToolInvocationPreview(
    ToolInvocationPlan Plan,
    IReadOnlyDictionary<string, ToolParameterValue> Parameters,
    CapabilityApprovalDisposition Disposition,
    DateTimeOffset ExpiresAt,
    CorrelationId CorrelationId,
    string PreviewHash);

internal sealed record ReadyConversationToolPreview(
    RunConversationId ConversationId,
    RunConversationTurnId TurnId,
    string ToolCallId,
    ReadyToolInvocationPreview Invocation);

internal sealed record CreatedReadyAdminSession(string Token, ReadyAdminSession Session);

internal sealed class ReadyAdminSessionManager
{
    public const string CookieName = "agentforge.admin";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, ReadyAdminSession> _sessions = new(StringComparer.Ordinal);

    public CreatedReadyAdminSession Create(
        InstallationId installationId,
        ActorId actorId,
        DateTimeOffset now)
    {
        lock (_sessions)
        {
            RemoveExpired(now);
            foreach (var existing in _sessions.Where(item => item.Value.InstallationId == installationId))
            {
                _sessions.TryRemove(existing.Key, out _);
            }

            var token = Token();
            var hash = Hash(token);
            var session = new ReadyAdminSession(
                hash,
                Token(),
                installationId,
                actorId,
                now.Add(Lifetime));
            _sessions[hash] = session;
            return new CreatedReadyAdminSession(token, session);
        }
    }

    public ReadyAdminSession? Validate(
        string? token,
        string? csrf,
        DateTimeOffset now,
        bool requireCsrf)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256)
        {
            return null;
        }

        var hash = Hash(token);
        if (!_sessions.TryGetValue(hash, out var session))
        {
            return null;
        }

        if (session.ExpiresAtUtc <= now)
        {
            _sessions.TryRemove(hash, out _);
            return null;
        }

        return !requireCsrf || csrf is not null && FixedEquals(csrf, session.CsrfToken)
            ? session
            : null;
    }

    public void Revoke(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token) && token.Length <= 256)
        {
            _sessions.TryRemove(Hash(token), out _);
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var entry in _sessions)
        {
            if (entry.Value.ExpiresAtUtc <= now)
            {
                _sessions.TryRemove(entry.Key, out _);
            }
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
            CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
