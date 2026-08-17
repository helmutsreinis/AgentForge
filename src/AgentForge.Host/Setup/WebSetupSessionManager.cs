using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain.Providers;

namespace AgentForge.Host.Setup;

internal sealed record PendingWebProvider(string Name, string ProviderType, Uri Endpoint, string? Model);

internal sealed class WebSetupSession
{
    public required string Hash { get; init; }
    public required string CsrfToken { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public PendingWebProvider? PendingProvider { get; set; }
    public IReadOnlyList<string> AvailableModels { get; set; } = [];
    public ProviderProfileId? ProviderId { get; set; }
    public bool Begun { get; set; }
    public bool ModelTested { get; set; }
    public bool AgentCreated { get; set; }
    public bool Completed { get; set; }
    public ConcurrentDictionary<string, WebIdempotencyResult> Results { get; } = new(StringComparer.Ordinal);
    public SemaphoreSlim MutationGate { get; } = new(1, 1);
}

internal sealed record WebIdempotencyResult(string RequestHash, object Response);
internal sealed record CreatedWebSession(string Token, string CsrfToken, WebSetupSession Session, bool Resumed);

internal sealed class WebSetupSessionManager
{
    public const string CookieName = "agentforge.setup";
    private readonly ConcurrentDictionary<string, WebSetupSession> _sessions = new(StringComparer.Ordinal);
    private readonly object _creationGate = new();
    private string? _oneTimeGrant = Token();

    public CreatedWebSession? CreateOrResume(string? existingToken, DateTimeOffset now)
    {
        var resumed = Validate(existingToken, null, now, false);
        if (resumed is not null)
        {
            return new CreatedWebSession(existingToken!, resumed.CsrfToken, resumed, true);
        }

        lock (_creationGate)
        {
            RemoveExpired(now);
            if (_sessions.Values.Any(session => !session.Completed))
            {
                return null;
            }

            _oneTimeGrant ??= Token();
            _oneTimeGrant = null;
            var token = Token();
            var hash = Hash(token);
            var session = new WebSetupSession
            {
                Hash = hash,
                CsrfToken = Token(),
                ExpiresAtUtc = now.AddMinutes(30),
            };
            _sessions[hash] = session;
            return new CreatedWebSession(token, session.CsrfToken, session, false);
        }
    }

    public WebSetupSession? Validate(string? token, string? csrf, DateTimeOffset now, bool requireCsrf)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256) return null;
        var hash = Hash(token);
        if (!_sessions.TryGetValue(hash, out var session)) return null;
        if (session.ExpiresAtUtc <= now)
        {
            _sessions.TryRemove(hash, out _);
            return null;
        }
        return !requireCsrf || csrf is not null && FixedEquals(csrf, session.CsrfToken) ? session : null;
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

        if (_sessions.IsEmpty)
        {
            _oneTimeGrant = Token();
        }
    }

    public static string Hash(string value) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";

    private static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
