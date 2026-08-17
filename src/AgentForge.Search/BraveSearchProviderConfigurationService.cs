using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Search;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Search;
using AgentForge.Domain.Security;

namespace AgentForge.Search;

internal sealed class BraveSearchProviderConfigurationService(
    ISearchProviderProfileRepository profiles,
    IBraveSearchConnectivityProbe probe,
    ISecretStore secretStore,
    IAuditRecorder audit,
    IUnitOfWork unitOfWork,
    IClock clock) : IBraveSearchProviderConfigurationService
{
    private static readonly Uri Endpoint = new("https://api.search.brave.com/res/v1/web/search");
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ValueTask<SearchProviderProfile?> FindAsync(
        InstallationId installationId,
        CancellationToken cancellationToken) =>
        profiles.FindAsync(installationId, "brave", cancellationToken);

    public async Task<DomainResult<BraveSearchConfigurationPreview>> PreviewAsync(
        InstallationId installationId,
        long? expectedVersion,
        BraveSearchConfigurationCandidate candidate,
        ReadOnlyMemory<char> credential,
        ActorId actorId,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(candidate);
        if (!normalized.IsSuccess)
        {
            return DomainResult.Fail<BraveSearchConfigurationPreview>(normalized.Failure!);
        }
        if (installationId.Value == Guid.Empty || !Text(actorId.Value, 256) || !Text(correlationId.Value, 128))
        {
            return Invalid<BraveSearchConfigurationPreview>("Brave configuration identity is invalid.");
        }

        var current = await profiles.FindAsync(installationId, "brave", cancellationToken);
        var conflict = current is null ? expectedVersion.HasValue : expectedVersion != current.Version;
        if (conflict)
        {
            return Conflict<BraveSearchConfigurationPreview>(
                "The Brave Search configuration changed; refresh before previewing another version.");
        }

        var usesNewCredential = !credential.IsEmpty;
        if (!ValidCredential(credential.Span) && usesNewCredential || current is null && !usesNewCredential)
        {
            return Invalid<BraveSearchConfigurationPreview>(
                "A bounded Brave API key is required when creating or rotating the provider.");
        }
        if ((usesNewCredential || normalized.Value.IsEnabled) && !secretStore.GetCapability().IsAvailable)
        {
            return DomainResult.Fail<BraveSearchConfigurationPreview>(new DomainFailure(
                FailureCode.UnsupportedCapability,
                "The OS-backed secret store is unavailable."));
        }

        var fingerprint = usesNewCredential
            ? CredentialFingerprint(credential.Span)
            : $"retained:{current!.EvidenceHash}";
        BraveSearchProbeEvidence? evidence = null;
        if (normalized.Value.IsEnabled || usesNewCredential)
        {
            var probed = usesNewCredential
                ? await probe.ProbeAsync(credential, normalized.Value, cancellationToken)
                : await ProbeExistingAsync(current!, normalized.Value, cancellationToken);
            if (!probed.IsSuccess)
            {
                return DomainResult.Fail<BraveSearchConfigurationPreview>(probed.Failure!);
            }
            evidence = probed.Value;
        }

        var requestHash = Hash(new
        {
            Kind = "brave-search-configuration-v1",
            InstallationId = installationId.Value,
            ExpectedVersion = expectedVersion,
            CurrentEvidence = current?.EvidenceHash,
            Candidate = normalized.Value,
            UsesNewCredential = usesNewCredential,
            CredentialFingerprint = fingerprint,
            ActorId = actorId.Value,
            CorrelationId = correlationId.Value,
        });
        return DomainResult.Success(new BraveSearchConfigurationPreview(
            installationId,
            expectedVersion,
            normalized.Value,
            usesNewCredential,
            fingerprint,
            requestHash,
            evidence,
            actorId,
            correlationId));
    }

    public async Task<DomainResult<BraveSearchConfigurationResult>> ApplyAsync(
        BraveSearchConfigurationPreview preview,
        ReadOnlyMemory<char> credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var suppliedFingerprint = credential.IsEmpty ? string.Empty : CredentialFingerprint(credential.Span);
        if (preview.UsesNewCredential != !credential.IsEmpty ||
            preview.UsesNewCredential && (!ValidCredential(credential.Span) ||
                !string.Equals(preview.CredentialFingerprint, suppliedFingerprint, StringComparison.Ordinal)))
        {
            return DomainResult.Fail<BraveSearchConfigurationResult>(new DomainFailure(
                FailureCode.PolicyDenied,
                "Apply requires the exact credential used by the approved preview."));
        }

        var current = await profiles.FindAsync(preview.InstallationId, "brave", cancellationToken);
        var conflict = current is null ? preview.ExpectedVersion.HasValue : preview.ExpectedVersion != current.Version;
        if (conflict)
        {
            return Conflict<BraveSearchConfigurationResult>(
                "The Brave Search configuration changed after preview.");
        }
        var expectedHash = Hash(new
        {
            Kind = "brave-search-configuration-v1",
            InstallationId = preview.InstallationId.Value,
            ExpectedVersion = preview.ExpectedVersion,
            CurrentEvidence = current?.EvidenceHash,
            Candidate = preview.Candidate,
            UsesNewCredential = preview.UsesNewCredential,
            CredentialFingerprint = preview.CredentialFingerprint,
            ActorId = preview.ActorId.Value,
            CorrelationId = preview.CorrelationId.Value,
        });
        if (!string.Equals(expectedHash, preview.RequestHash, StringComparison.Ordinal))
        {
            return DomainResult.Fail<BraveSearchConfigurationResult>(new DomainFailure(
                FailureCode.PolicyDenied,
                "The approved Brave Search preview is not valid for this configuration."));
        }

        if (preview.Candidate.IsEnabled || preview.UsesNewCredential)
        {
            var probed = preview.UsesNewCredential
                ? await probe.ProbeAsync(credential, preview.Candidate, cancellationToken)
                : await ProbeExistingAsync(current!, preview.Candidate, cancellationToken);
            if (!probed.IsSuccess)
            {
                return DomainResult.Fail<BraveSearchConfigurationResult>(probed.Failure!);
            }
        }

        var newReference = current?.CredentialReference ?? SecretReference.NoCredential;
        if (preview.UsesNewCredential)
        {
            var stored = await secretStore.StoreAsync(
                $"search-brave-{Guid.NewGuid():N}", credential, cancellationToken);
            if (!stored.IsSuccess)
            {
                return DomainResult.Fail<BraveSearchConfigurationResult>(stored.Failure!);
            }
            newReference = stored.Value;
        }

        var now = clock.UtcNow;
        var profile = new SearchProviderProfile(
            preview.InstallationId,
            "brave",
            SearchProviderKind.Brave,
            Endpoint,
            newReference,
            preview.Candidate.IsEnabled,
            preview.Candidate.SafeSearch,
            preview.Candidate.CountryCode,
            preview.Candidate.SearchLanguage,
            current is null ? 0 : current.Version + 1,
            current?.CreatedAtUtc ?? now,
            now,
            preview.ActorId,
            preview.CorrelationId);
        try
        {
            if (current is null)
            {
                await profiles.AddAsync(profile, cancellationToken);
            }
            else
            {
                await profiles.UpdateAsync(profile, current.Version, cancellationToken);
            }
            await audit.RecordAsync(new AuditRecordRequest(
                preview.InstallationId,
                preview.ActorId,
                preview.CorrelationId,
                null,
                current is null ? "search.brave.configured" : "search.brave.updated",
                AuditOutcome.Succeeded,
                new
                {
                    ProviderId = profile.Id,
                    profile.IsEnabled,
                    SafeSearch = profile.SafeSearch.ToString(),
                    profile.CountryCode,
                    profile.SearchLanguage,
                    CredentialRotated = preview.UsesNewCredential,
                    ExpectedVersion = preview.ExpectedVersion,
                },
                new { profile.Version, profile.EvidenceHash },
                null), cancellationToken);
            var commit = await unitOfWork.CommitAsync(cancellationToken);
            if (!commit.Succeeded)
            {
                if (preview.UsesNewCredential)
                {
                    _ = await secretStore.DeleteAsync(newReference, CancellationToken.None);
                }
                return DomainResult.Fail<BraveSearchConfigurationResult>(commit.Failure!);
            }
        }
        catch
        {
            if (preview.UsesNewCredential)
            {
                _ = await secretStore.DeleteAsync(newReference, CancellationToken.None);
            }
            throw;
        }

        if (preview.UsesNewCredential && current is not null &&
            current.CredentialReference != newReference)
        {
            _ = await secretStore.DeleteAsync(current.CredentialReference, CancellationToken.None);
        }
        return DomainResult.Success(new BraveSearchConfigurationResult(
            profile,
            preview.RequestHash,
            preview.UsesNewCredential));
    }

    private async Task<DomainResult<BraveSearchProbeEvidence>> ProbeExistingAsync(
        SearchProviderProfile profile,
        BraveSearchConfigurationCandidate candidate,
        CancellationToken cancellationToken)
    {
        var materialized = await secretStore.MaterializeAsync(profile.CredentialReference, cancellationToken);
        if (!materialized.IsSuccess)
        {
            return DomainResult.Fail<BraveSearchProbeEvidence>(materialized.Failure!);
        }
        await using var lease = materialized.Value;
        return await probe.ProbeAsync(lease.Value, candidate, cancellationToken);
    }

    private static DomainResult<BraveSearchConfigurationCandidate> Normalize(
        BraveSearchConfigurationCandidate candidate)
    {
        if (candidate is null || !Enum.IsDefined(candidate.SafeSearch))
        {
            return Invalid<BraveSearchConfigurationCandidate>("Brave Search policy is invalid.");
        }
        var country = candidate.CountryCode?.Trim().ToUpperInvariant() ?? string.Empty;
        var language = candidate.SearchLanguage?.Trim().ToLowerInvariant() ?? string.Empty;
        if (country.Length != 0 && (country.Length != 2 || !country.All(char.IsAsciiLetter)) ||
            language.Length is < 2 or > 16 ||
            !language.All(character => char.IsAsciiLetter(character) || character == '-'))
        {
            return Invalid<BraveSearchConfigurationCandidate>(
                "Country must be blank or a two-letter code and search language must be bounded.");
        }
        return DomainResult.Success(candidate with { CountryCode = country, SearchLanguage = language });
    }

    private static bool ValidCredential(ReadOnlySpan<char> credential) =>
        credential.Length is >= 1 and <= 512 &&
        !credential.Contains('\r') && !credential.Contains('\n') && !credential.Contains('\0');

    private static bool Text(string value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);

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

    private static string Hash<T>(T value) => SearchContractValidator.Hash(
        JsonSerializer.Serialize(value, SerializerOptions));

    private static DomainResult<T> Invalid<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<T> Conflict<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ConcurrencyConflict, message));
}
