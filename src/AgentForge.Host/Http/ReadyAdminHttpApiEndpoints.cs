using System.Security.Cryptography;
using System.Text;
using AgentForge.Abstractions.HttpApi;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.HttpApi;
using AgentForge.Domain.Primitives;

namespace AgentForge.Host.Http;

internal sealed record ReadyHttpApiPreviewRequest(
    long? ExpectedVersion,
    string ProfileId,
    string DisplayName,
    string BaseEndpoint,
    string ProbeRelativePath,
    IReadOnlyDictionary<string, string>? StaticHeaders,
    bool IsEnabled,
    string? BearerToken);

internal sealed record ReadyHttpApiApplyRequest(string PreviewHash, string? BearerToken);

internal static partial class ReadyAdminEndpoints
{
    private static async Task<IResult> ListHttpApiProfilesAsync(
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IHttpApiConfigurationService configuration,
        ISecretStore secretStore,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireAsync(context, sessions, stateReader, clock, false, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        var profiles = await configuration.ListAsync(acquired.Session!.InstallationId, cancellationToken);
        var capability = secretStore.GetCapability();
        return Results.Ok(new
        {
            profiles = profiles.Select(profile => new
            {
                profileId = profile.Id.Value,
                profile.DisplayName,
                baseEndpoint = profile.BaseEndpoint.AbsoluteUri,
                profile.ProbeRelativePath,
                profile.StaticHeaders,
                profile.IsEnabled,
                profile.Version,
                profile.EvidenceHash,
                authentication = "Write-only OS-backed bearer token",
                profile.UpdatedAtUtc,
            }),
            secretStore = new { capability.Store, capability.IsAvailable, reason = capability.UnavailableReason?.Message },
            correlationId = context.TraceIdentifier,
        });
    }

    private static async Task<IResult> PreviewHttpApiProfileAsync(
        ReadyHttpApiPreviewRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IHttpApiConfigurationService configuration,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        if (request is null || !Uri.TryCreate(request.BaseEndpoint, UriKind.Absolute, out var endpoint) ||
            request.BearerToken is { Length: > 32768 } || (request.StaticHeaders?.Count ?? 0) > 16)
        {
            return Problem(context, 400, "Invalid HTTP API profile",
                "Provide a bounded profile ID, HTTPS base endpoint, relative probe path, headers, and write-only bearer token.",
                "validation-failure");
        }
        var token = string.IsNullOrEmpty(request.BearerToken)
            ? ReadOnlyMemory<char>.Empty : request.BearerToken.AsMemory();
        var fingerprint = WebCredentialFingerprint(token.Span);
        var session = acquired.Session!;
        var webHash = SnapshotHash(new
        {
            request.ExpectedVersion,
            request.ProfileId,
            request.DisplayName,
            BaseEndpoint = endpoint.AbsoluteUri,
            request.ProbeRelativePath,
            request.StaticHeaders,
            request.IsEnabled,
            CredentialFingerprint = fingerprint,
        });
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"http-api-preview:{request.ProfileId}:{idempotencyKey}";
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, webHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another HTTP API preview.", "idempotency-conflict");
            }
            var stable = StableRequestIdentity(session.InstallationId, "http-api-profile-preview", idempotencyKey);
            var correlation = new CorrelationId($"admin-http-api:{Convert.ToHexStringLower(stable.AsSpan(0, 16))}");
            var preview = await configuration.PreviewAsync(
                session.InstallationId,
                request.ExpectedVersion,
                new HttpApiConfigurationCandidate(
                    new HttpApiProfileId((request.ProfileId ?? string.Empty).Trim()),
                    (request.DisplayName ?? string.Empty).Trim(),
                    endpoint,
                    (request.ProbeRelativePath ?? string.Empty).Trim(),
                    request.StaticHeaders ?? new Dictionary<string, string>(),
                    request.IsEnabled),
                token,
                session.ActorId,
                correlation,
                cancellationToken);
            if (!preview.IsSuccess) return DomainProblem(context, preview.Failure!, "HTTP API verification failed");
            RetainBoundedPreviews(session.HttpApiPreviews, 16);
            session.HttpApiPreviews[preview.Value.RequestHash] = preview.Value;
            var response = new
            {
                previewHash = preview.Value.RequestHash,
                expectedVersion = preview.Value.ExpectedVersion,
                profileId = preview.Value.Candidate.Id.Value,
                preview.Value.Candidate.DisplayName,
                baseEndpoint = preview.Value.Candidate.BaseEndpoint.AbsoluteUri,
                preview.Value.Candidate.ProbeRelativePath,
                preview.Value.Candidate.StaticHeaders,
                preview.Value.Candidate.IsEnabled,
                credentialAction = preview.Value.UsesNewCredential
                    ? "Create or rotate OS-backed bearer token" : "Retain current OS-backed bearer token",
                verification = preview.Value.Probe is null ? null : new
                {
                    endpoint = preview.Value.Probe.Endpoint.AbsoluteUri,
                    preview.Value.Probe.StatusCode,
                    preview.Value.Probe.ResponseBytes,
                    durationMilliseconds = Math.Max(0, preview.Value.Probe.Duration.TotalMilliseconds),
                    preview.Value.Probe.EvidenceHash,
                },
                warning = "This configures a reusable credential profile only. Generated skill, agent grant, tool grant, and each exact GET remain separate approvals.",
                correlationId = correlation.Value,
            };
            StoreIdempotentResult(session, scopedKey, webHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static async Task<IResult> ApplyHttpApiProfileAsync(
        ReadyHttpApiApplyRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IHttpApiConfigurationService configuration,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        var session = acquired.Session!;
        var token = string.IsNullOrEmpty(request.BearerToken)
            ? ReadOnlyMemory<char>.Empty : request.BearerToken.AsMemory();
        var webHash = SnapshotHash(new { request.PreviewHash, CredentialFingerprint = WebCredentialFingerprint(token.Span) });
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"http-api-apply:{idempotencyKey}";
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, webHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another HTTP API update.", "idempotency-conflict");
            }
            if (!Text(request.PreviewHash, 128) ||
                !session.HttpApiPreviews.TryGetValue(request.PreviewHash, out var preview))
            {
                return Problem(context, 403, "Approved HTTP API preview required",
                    "Verify the exact endpoint, headers, probe, and credential action before applying them.", "policy-denied");
            }
            var applied = await configuration.ApplyAsync(preview, token, cancellationToken);
            if (!applied.IsSuccess) return DomainProblem(context, applied.Failure!, "HTTP API profile update failed");
            var response = new
            {
                configured = true,
                profileId = applied.Value.Profile.Id.Value,
                applied.Value.Profile.DisplayName,
                baseEndpoint = applied.Value.Profile.BaseEndpoint.AbsoluteUri,
                applied.Value.Profile.ProbeRelativePath,
                applied.Value.Profile.StaticHeaders,
                applied.Value.Profile.IsEnabled,
                applied.Value.Profile.Version,
                applied.Value.Profile.EvidenceHash,
                applied.Value.CredentialRotated,
                authentication = "Write-only OS-backed bearer token",
                correlationId = preview.CorrelationId.Value,
            };
            session.HttpApiPreviews.TryRemove(request.PreviewHash, out _);
            StoreIdempotentResult(session, scopedKey, webHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static string WebCredentialFingerprint(ReadOnlySpan<char> token)
    {
        if (token.IsEmpty) return "retained";
        var bytes = new byte[Encoding.UTF8.GetByteCount(token)];
        try
        {
            Encoding.UTF8.GetBytes(token, bytes);
            return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
