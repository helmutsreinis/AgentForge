using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.HttpApi;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.HttpApi;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;

namespace AgentForge.HttpApi;

internal sealed class HttpApiConfigurationService(
    IHttpApiProfileRepository profiles,
    IHttpApiConnectivityProbe probe,
    ISecretStore secretStore,
    IAuditRecorder audit,
    IUnitOfWork unitOfWork,
    ISensitiveDataRedactor redactor,
    IClock clock) : IHttpApiConfigurationService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ValueTask<HttpApiProfile?> FindAsync(
        InstallationId installationId,
        HttpApiProfileId profileId,
        CancellationToken cancellationToken) => profiles.FindAsync(installationId, profileId, cancellationToken);

    public Task<IReadOnlyList<HttpApiProfile>> ListAsync(
        InstallationId installationId,
        CancellationToken cancellationToken) => profiles.ListAsync(installationId, cancellationToken);

    public async Task<DomainResult<HttpApiConfigurationPreview>> PreviewAsync(
        InstallationId installationId,
        long? expectedVersion,
        HttpApiConfigurationCandidate candidate,
        ReadOnlyMemory<char> bearerToken,
        ActorId actorId,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        var normalized = HttpApiContract.Normalize(candidate);
        if (!normalized.IsSuccess || installationId.Value == Guid.Empty ||
            !Text(actorId.Value, 256) || !Text(correlationId.Value, 128) ||
            normalized.IsSuccess && redactor.Redact(normalized.Value.StaticHeaders).ContainsRedactions)
        {
            return Invalid<HttpApiConfigurationPreview>(
                normalized.Failure?.Message ?? "The HTTP API configuration identity is invalid.");
        }
        var current = await profiles.FindAsync(installationId, normalized.Value.Id, cancellationToken);
        if (current is null ? expectedVersion.HasValue : expectedVersion != current.Version)
        {
            return Conflict<HttpApiConfigurationPreview>(
                "The HTTP API profile changed; reload before previewing another version.");
        }
        var usesNewCredential = !bearerToken.IsEmpty;
        if (usesNewCredential && !HttpApiContract.ValidBearer(bearerToken.Span) || current is null && !usesNewCredential)
        {
            return Invalid<HttpApiConfigurationPreview>(
                "A bounded write-only bearer token is required when creating or rotating the profile.");
        }
        if ((usesNewCredential || normalized.Value.IsEnabled) && !secretStore.GetCapability().IsAvailable)
        {
            return DomainResult.Fail<HttpApiConfigurationPreview>(new DomainFailure(
                FailureCode.UnsupportedCapability, "The OS-backed secret store is unavailable."));
        }

        var fingerprint = usesNewCredential ? CredentialFingerprint(bearerToken.Span) : $"retained:{current!.EvidenceHash}";
        HttpApiProbeEvidence? evidence = null;
        if (normalized.Value.IsEnabled || usesNewCredential)
        {
            var probed = usesNewCredential
                ? await probe.ProbeAsync(normalized.Value, bearerToken, cancellationToken)
                : await ProbeExistingAsync(current!, normalized.Value, cancellationToken);
            if (!probed.IsSuccess) return DomainResult.Fail<HttpApiConfigurationPreview>(probed.Failure!);
            evidence = probed.Value;
        }
        var requestHash = RequestHash(installationId, expectedVersion, current?.EvidenceHash,
            normalized.Value, usesNewCredential, fingerprint, actorId, correlationId);
        return DomainResult.Success(new HttpApiConfigurationPreview(
            installationId, expectedVersion, normalized.Value, usesNewCredential, fingerprint,
            requestHash, evidence, actorId, correlationId));
    }

    public async Task<DomainResult<HttpApiConfigurationResult>> ApplyAsync(
        HttpApiConfigurationPreview preview,
        ReadOnlyMemory<char> bearerToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.UsesNewCredential != !bearerToken.IsEmpty ||
            preview.UsesNewCredential && (!HttpApiContract.ValidBearer(bearerToken.Span) ||
                !string.Equals(preview.CredentialFingerprint, CredentialFingerprint(bearerToken.Span), StringComparison.Ordinal)))
        {
            return DomainResult.Fail<HttpApiConfigurationResult>(new DomainFailure(
                FailureCode.PolicyDenied, "Apply requires the exact bearer token used by the approved preview."));
        }
        var current = await profiles.FindAsync(
            preview.InstallationId, preview.Candidate.Id, cancellationToken);
        if (current is null ? preview.ExpectedVersion.HasValue : preview.ExpectedVersion != current.Version)
        {
            return Conflict<HttpApiConfigurationResult>("The HTTP API profile changed after preview.");
        }
        var expectedHash = RequestHash(preview.InstallationId, preview.ExpectedVersion, current?.EvidenceHash,
            preview.Candidate, preview.UsesNewCredential, preview.CredentialFingerprint,
            preview.ActorId, preview.CorrelationId);
        if (!string.Equals(expectedHash, preview.RequestHash, StringComparison.Ordinal))
        {
            return DomainResult.Fail<HttpApiConfigurationResult>(new DomainFailure(
                FailureCode.PolicyDenied, "The approved HTTP API preview is not valid for this configuration."));
        }
        if (preview.Candidate.IsEnabled || preview.UsesNewCredential)
        {
            var probed = preview.UsesNewCredential
                ? await probe.ProbeAsync(preview.Candidate, bearerToken, cancellationToken)
                : await ProbeExistingAsync(current!, preview.Candidate, cancellationToken);
            if (!probed.IsSuccess) return DomainResult.Fail<HttpApiConfigurationResult>(probed.Failure!);
        }

        var newReference = current?.CredentialReference ?? SecretReference.NoCredential;
        if (preview.UsesNewCredential)
        {
            var stored = await secretStore.StoreAsync(
                $"http-api-{preview.Candidate.Id.Value}-{Guid.NewGuid():N}", bearerToken, cancellationToken);
            if (!stored.IsSuccess) return DomainResult.Fail<HttpApiConfigurationResult>(stored.Failure!);
            newReference = stored.Value;
        }
        var now = clock.UtcNow;
        var profile = new HttpApiProfile(
            preview.InstallationId, preview.Candidate.Id, preview.Candidate.DisplayName,
            preview.Candidate.BaseEndpoint, preview.Candidate.ProbeRelativePath,
            preview.Candidate.StaticHeaders, newReference, preview.Candidate.IsEnabled,
            current is null ? 0 : current.Version + 1, current?.CreatedAtUtc ?? now, now,
            preview.ActorId, preview.CorrelationId);
        try
        {
            if (current is null) await profiles.AddAsync(profile, cancellationToken);
            else await profiles.UpdateAsync(profile, current.Version, cancellationToken);
            await audit.RecordAsync(new AuditRecordRequest(
                preview.InstallationId, preview.ActorId, preview.CorrelationId, null,
                current is null ? "http-api.profile-configured" : "http-api.profile-updated",
                AuditOutcome.Succeeded,
                new
                {
                    ProfileId = profile.Id.Value,
                    profile.DisplayName,
                    BaseEndpoint = profile.BaseEndpoint.AbsoluteUri,
                    profile.ProbeRelativePath,
                    StaticHeaderNames = profile.StaticHeaders.Keys.Order(StringComparer.OrdinalIgnoreCase),
                    profile.IsEnabled,
                    CredentialRotated = preview.UsesNewCredential,
                    preview.ExpectedVersion,
                },
                new { profile.Version, profile.EvidenceHash }, null), cancellationToken);
            var commit = await unitOfWork.CommitAsync(cancellationToken);
            if (!commit.Succeeded)
            {
                if (preview.UsesNewCredential) _ = await secretStore.DeleteAsync(newReference, CancellationToken.None);
                return DomainResult.Fail<HttpApiConfigurationResult>(commit.Failure!);
            }
        }
        catch
        {
            if (preview.UsesNewCredential) _ = await secretStore.DeleteAsync(newReference, CancellationToken.None);
            throw;
        }
        if (preview.UsesNewCredential && current is not null && current.CredentialReference != newReference)
        {
            _ = await secretStore.DeleteAsync(current.CredentialReference, CancellationToken.None);
        }
        return DomainResult.Success(new HttpApiConfigurationResult(profile, preview.RequestHash, preview.UsesNewCredential));
    }

    private async Task<DomainResult<HttpApiProbeEvidence>> ProbeExistingAsync(
        HttpApiProfile current,
        HttpApiConfigurationCandidate candidate,
        CancellationToken cancellationToken)
    {
        var materialized = await secretStore.MaterializeAsync(current.CredentialReference, cancellationToken);
        if (!materialized.IsSuccess) return DomainResult.Fail<HttpApiProbeEvidence>(materialized.Failure!);
        await using var lease = materialized.Value;
        return await probe.ProbeAsync(candidate, lease.Value, cancellationToken);
    }

    private static string RequestHash(
        InstallationId installationId,
        long? expectedVersion,
        string? currentEvidence,
        HttpApiConfigurationCandidate candidate,
        bool usesNewCredential,
        string fingerprint,
        ActorId actorId,
        CorrelationId correlationId) => Hash(new
        {
            Kind = "http-api-configuration-v1",
            InstallationId = installationId.Value,
            ExpectedVersion = expectedVersion,
            CurrentEvidence = currentEvidence,
            Candidate = candidate,
            UsesNewCredential = usesNewCredential,
            CredentialFingerprint = fingerprint,
            ActorId = actorId.Value,
            CorrelationId = correlationId.Value,
        });

    private static string CredentialFingerprint(ReadOnlySpan<char> credential)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(credential)];
        try
        {
            Encoding.UTF8.GetBytes(credential, bytes);
            return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string Hash<T>(T value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)))}";

    private static bool Text(string value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(char.IsControl);

    private static DomainResult<T> Invalid<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<T> Conflict<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ConcurrencyConflict, message));
}
