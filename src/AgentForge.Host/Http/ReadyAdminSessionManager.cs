using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain.Primitives;

namespace AgentForge.Host.Http;

internal sealed record ReadyAdminSession(
    string Hash,
    string CsrfToken,
    InstallationId InstallationId,
    ActorId ActorId,
    DateTimeOffset ExpiresAtUtc)
{
    public ConcurrentDictionary<string, ReadyAdminIdempotencyResult> Results { get; } = new(StringComparer.Ordinal);

    public SemaphoreSlim MutationGate { get; } = new(1, 1);
}

internal sealed record ReadyAdminIdempotencyResult(string RequestHash, object Response);

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
