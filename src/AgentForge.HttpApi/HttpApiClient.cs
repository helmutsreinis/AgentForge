using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Abstractions.HttpApi;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.HttpApi;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;

namespace AgentForge.HttpApi;

internal sealed class HttpApiClient : IDisposable
{
    private const int AbsoluteMaximumResponseBytes = 1_048_576;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HttpClient _client;
    private readonly IClock _clock;

    private HttpApiClient(HttpMessageHandler handler, IClock clock)
    {
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _clock = clock;
    }

    public static HttpApiClient Create(IClock clock) => new(CreateHandler(), clock);

    internal static HttpApiClient CreateForTesting(HttpMessageHandler handler, IClock clock) => new(handler, clock);

    public async Task<DomainResult<HttpApiReadResponse>> GetAsync(
        HttpApiProfile profile,
        ReadOnlyMemory<char> bearerToken,
        HttpApiReadRequest request,
        CancellationToken cancellationToken)
    {
        if (!HttpApiContract.TryNormalizeProfile(profile, out _) ||
            !HttpApiContract.ValidBearer(bearerToken.Span) ||
            request.MaximumResponseBytes is < 1 or > AbsoluteMaximumResponseBytes ||
            !Guid.TryParse(request.CorrelationId, out var correlation) || correlation == Guid.Empty ||
            !Guid.TryParse(request.RequestId, out var requestId) || requestId == Guid.Empty)
        {
            return Invalid("The HTTP API profile, bearer credential, or read request is invalid.");
        }
        var endpoint = HttpApiContract.BuildEndpoint(profile.BaseEndpoint, request.RelativePath, request.Query);
        if (!endpoint.IsSuccess)
        {
            return DomainResult.Fail<HttpApiReadResponse>(endpoint.Failure!);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = bearerToken.ToString();
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, endpoint.Value);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            message.Headers.TryAddWithoutValidation("X-Correlation-Id", request.CorrelationId);
            message.Headers.TryAddWithoutValidation("X-Request-Id", request.RequestId);
            message.Headers.TryAddWithoutValidation("User-Agent", "AgentForge/1.0");
            foreach (var header in profile.StaticHeaders)
            {
                var value = ExpandHeaderTemplate(header.Value, request.CorrelationId, request.RequestId);
                if (!message.Headers.TryAddWithoutValidation(header.Key, value))
                {
                    return Invalid("A configured HTTP API header could not be applied safely.");
                }
            }

            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Denied("The configured HTTP API rejected the bearer authority.");
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                return External("The configured HTTP API is temporarily unavailable.", true);
            }
            if (!response.IsSuccessStatusCode)
            {
                return External($"The configured HTTP API rejected the bounded GET request with status {(int)response.StatusCode}.", false);
            }

            var payload = await ReadBoundedAsync(response, request.MaximumResponseBytes, timeout.Token);
            if (!payload.IsSuccess)
            {
                return DomainResult.Fail<HttpApiReadResponse>(payload.Failure!);
            }
            string body;
            try
            {
                body = StrictUtf8.GetString(payload.Value);
            }
            catch (DecoderFallbackException)
            {
                return External("The configured HTTP API returned non-UTF-8 content.", false);
            }
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var bodyHash = Hash(payload.Value);
            var evidence = Hash(Encoding.UTF8.GetBytes(string.Join('\n',
                "http-api-read-v1", profile.Id.Value, endpoint.Value.AbsoluteUri,
                ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture),
                contentType, bodyHash, _clock.UtcNow.ToString("O"))));
            return DomainResult.Success(new HttpApiReadResponse(
                endpoint.Value, (int)response.StatusCode, contentType, body, evidence));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return External("The configured HTTP API timed out.", true);
        }
        catch (HttpRequestException)
        {
            return External("The configured HTTP API could not be reached.", true);
        }
        finally
        {
            token = string.Empty;
        }
    }

    public void Dispose() => _client.Dispose();

    private static string ExpandHeaderTemplate(string value, string correlationId, string requestId) =>
        value.Replace("{correlationId}", correlationId, StringComparison.Ordinal)
            .Replace("{requestId}", requestId, StringComparison.Ordinal);

    private static async Task<DomainResult<byte[]>> ReadBoundedAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            return DomainResult.Fail<byte[]>(new DomainFailure(
                FailureCode.RecoverableExternalFailure, "The external response exceeded its byte limit."));
        }
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var target = new MemoryStream(Math.Min(maximumBytes, 65_536));
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (target.Length + read > maximumBytes)
            {
                return DomainResult.Fail<byte[]>(new DomainFailure(
                    FailureCode.RecoverableExternalFailure, "The external response exceeded its byte limit."));
            }
            target.Write(buffer, 0, read);
        }
        return DomainResult.Success(target.ToArray());
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    private static string Hash(ReadOnlySpan<byte> value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(value))}";

    private static DomainResult<HttpApiReadResponse> Invalid(string message) =>
        DomainResult.Fail<HttpApiReadResponse>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<HttpApiReadResponse> Denied(string message) =>
        DomainResult.Fail<HttpApiReadResponse>(new DomainFailure(FailureCode.PolicyDenied, message));

    private static DomainResult<HttpApiReadResponse> External(string message, bool retryable) =>
        DomainResult.Fail<HttpApiReadResponse>(new DomainFailure(
            FailureCode.RecoverableExternalFailure, message, IsRetryable: retryable));
}

internal static class HttpApiContract
{
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Host", "Cookie", "Set-Cookie", "Content-Length",
        "Transfer-Encoding", "Connection", "Upgrade",
    };

    public static bool TryNormalizeProfile(HttpApiProfile profile, out HttpApiProfile normalized)
    {
        normalized = profile;
        if (!ValidId(profile.Id) || !Text(profile.DisplayName, 128) ||
            !TryNormalizeBase(profile.BaseEndpoint, out var endpoint) ||
            !ValidRelativePath(profile.ProbeRelativePath) || !ValidHeaders(profile.StaticHeaders)) return false;
        normalized = profile with
        {
            DisplayName = profile.DisplayName.Trim(),
            BaseEndpoint = endpoint,
            ProbeRelativePath = profile.ProbeRelativePath.Trim(),
            StaticHeaders = profile.StaticHeaders.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(item => item.Key.Trim(), item => item.Value.Trim(), StringComparer.OrdinalIgnoreCase),
        };
        return true;
    }

    public static DomainResult<HttpApiConfigurationCandidate> Normalize(HttpApiConfigurationCandidate candidate)
    {
        if (candidate is null || !ValidId(candidate.Id) || !Text(candidate.DisplayName, 128) ||
            !TryNormalizeBase(candidate.BaseEndpoint, out var endpoint) ||
            !ValidRelativePath(candidate.ProbeRelativePath) || !ValidHeaders(candidate.StaticHeaders))
        {
            return DomainResult.Fail<HttpApiConfigurationCandidate>(new DomainFailure(
                FailureCode.ValidationFailure,
                "The HTTP API profile requires a safe ID, HTTPS base endpoint, relative probe path, and bounded non-secret headers."));
        }
        return DomainResult.Success(candidate with
        {
            DisplayName = candidate.DisplayName.Trim(),
            BaseEndpoint = endpoint,
            ProbeRelativePath = candidate.ProbeRelativePath.Trim(),
            StaticHeaders = candidate.StaticHeaders.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(item => item.Key.Trim(), item => item.Value.Trim(), StringComparer.OrdinalIgnoreCase),
        });
    }

    public static DomainResult<Uri> BuildEndpoint(
        Uri baseEndpoint,
        string relativePath,
        IReadOnlyDictionary<string, string> query)
    {
        if (!TryNormalizeBase(baseEndpoint, out var normalizedBase) || !ValidRelativePath(relativePath) ||
            query is null || query.Count > 32 || query.Any(item => !QueryText(item.Key, 128) || !QueryText(item.Value, 2048)))
        {
            return DomainResult.Fail<Uri>(new DomainFailure(
                FailureCode.ValidationFailure, "The HTTP API path or query is outside its configured bound."));
        }
        var endpoint = new Uri(normalizedBase, relativePath.Trim());
        if (!SameOrigin(normalizedBase, endpoint) || !endpoint.AbsolutePath.StartsWith(
                normalizedBase.AbsolutePath, StringComparison.Ordinal))
        {
            return DomainResult.Fail<Uri>(new DomainFailure(
                FailureCode.PolicyDenied, "The HTTP API request escaped its configured endpoint."));
        }
        if (query.Count == 0) return DomainResult.Success(endpoint);
        var builder = new UriBuilder(endpoint)
        {
            Query = string.Join('&', query.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}")),
        };
        return DomainResult.Success(builder.Uri);
    }

    public static bool ValidBearer(ReadOnlySpan<char> value) => value.Length is >= 1 and <= 32_768 &&
        !value.Contains('\r') && !value.Contains('\n') && !value.Contains('\0');

    private static bool ValidId(HttpApiProfileId id) => id.Value is { Length: >= 3 and <= 64 } &&
        id.Value.All(character => character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) ||
            character is '-' or '_' or '.');

    private static bool TryNormalizeBase(Uri? value, out Uri normalized)
    {
        normalized = null!;
        if (value is null || !value.IsAbsoluteUri || value.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(value.Host) || !string.IsNullOrEmpty(value.UserInfo) ||
            !string.IsNullOrEmpty(value.Query) || !string.IsNullOrEmpty(value.Fragment) ||
            value.AbsoluteUri.Length > 2048) return false;
        var text = value.AbsoluteUri.EndsWith('/') ? value.AbsoluteUri : value.AbsoluteUri + "/";
        normalized = new Uri(text, UriKind.Absolute);
        return true;
    }

    private static bool ValidRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.StartsWith('/') ||
            value.StartsWith("//", StringComparison.Ordinal) || Uri.TryCreate(value, UriKind.Absolute, out _) ||
            value.Any(character => char.IsControl(character) || character == '\\' || character == '#')) return false;
        try
        {
            var path = value.Split('?', 2)[0];
            var decoded = Uri.UnescapeDataString(path);
            return !decoded.StartsWith('/') && !decoded.Contains('\\') &&
                !decoded.Split('/').Any(part => part is "." or "..");
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool ValidHeaders(IReadOnlyDictionary<string, string> headers) => headers is not null &&
        headers.Count <= 16 && headers.All(item => !ForbiddenHeaders.Contains(item.Key) &&
            !SensitiveHeaderName(item.Key) && HeaderName(item.Key) &&
            Text(item.Value, 1024) && ValidHeaderTemplates(item.Value));

    private static bool SensitiveHeaderName(string value)
    {
        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalized.Contains("apikey", StringComparison.Ordinal) ||
            normalized.Contains("subscriptionkey", StringComparison.Ordinal) ||
            normalized.Contains("token", StringComparison.Ordinal) ||
            normalized.Contains("secret", StringComparison.Ordinal) ||
            normalized.Contains("credential", StringComparison.Ordinal) ||
            normalized.Contains("authorization", StringComparison.Ordinal);
    }

    private static bool ValidHeaderTemplates(string value)
    {
        var withoutKnownTemplates = value.Replace("{correlationId}", string.Empty, StringComparison.Ordinal)
            .Replace("{requestId}", string.Empty, StringComparison.Ordinal);
        return !withoutKnownTemplates.Contains('{') && !withoutKnownTemplates.Contains('}');
    }

    private static bool HeaderName(string value) => value is { Length: >= 1 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool QueryText(string value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(char.IsControl);

    private static bool Text(string value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(char.IsControl);

    private static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme == right.Scheme && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;
}

internal sealed class HttpApiReadService(ISecretStore secrets, HttpApiClient client) : IHttpApiReadService
{
    public async Task<DomainResult<HttpApiReadResponse>> GetAsync(
        HttpApiProfile profile,
        HttpApiReadRequest request,
        CancellationToken cancellationToken)
    {
        var materialized = await secrets.MaterializeAsync(profile.CredentialReference, cancellationToken);
        if (!materialized.IsSuccess) return DomainResult.Fail<HttpApiReadResponse>(materialized.Failure!);
        await using var lease = materialized.Value;
        return await client.GetAsync(profile, lease.Value, request, cancellationToken);
    }
}

internal sealed class HttpApiConnectivityProbe(HttpApiClient client, IClock clock) : IHttpApiConnectivityProbe
{
    public async Task<DomainResult<HttpApiProbeEvidence>> ProbeAsync(
        HttpApiConfigurationCandidate candidate,
        ReadOnlyMemory<char> bearerToken,
        CancellationToken cancellationToken)
    {
        var normalized = HttpApiContract.Normalize(candidate);
        if (!normalized.IsSuccess) return DomainResult.Fail<HttpApiProbeEvidence>(normalized.Failure!);
        var started = Stopwatch.GetTimestamp();
        var profile = new HttpApiProfile(
            new InstallationId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            normalized.Value.Id, normalized.Value.DisplayName, normalized.Value.BaseEndpoint,
            normalized.Value.ProbeRelativePath, normalized.Value.StaticHeaders,
            SecretReference.NoCredential, true, 0, clock.UtcNow, clock.UtcNow,
            new ActorId("http-api-probe"), new CorrelationId("http-api-probe"));
        var response = await client.GetAsync(profile, bearerToken, new HttpApiReadRequest(
            candidate.ProbeRelativePath, new Dictionary<string, string>(), 65_536,
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")), cancellationToken);
        if (!response.IsSuccess) return DomainResult.Fail<HttpApiProbeEvidence>(response.Failure!);
        return DomainResult.Success(new HttpApiProbeEvidence(
            response.Value.Endpoint, response.Value.StatusCode, Encoding.UTF8.GetByteCount(response.Value.Body),
            Stopwatch.GetElapsedTime(started), response.Value.EvidenceHash));
    }
}
