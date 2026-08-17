using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;

namespace AgentForge.Domain.HttpApi;

public readonly record struct HttpApiProfileId(string Value)
{
    public override string ToString() => Value;
}

public sealed record HttpApiProfile(
    InstallationId InstallationId,
    HttpApiProfileId Id,
    string DisplayName,
    Uri BaseEndpoint,
    string ProbeRelativePath,
    IReadOnlyDictionary<string, string> StaticHeaders,
    SecretReference CredentialReference,
    bool IsEnabled,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    ActorId ActorId,
    CorrelationId CorrelationId)
{
    public string EvidenceHash => Hash(string.Join('\n',
        "http-api-profile-v1",
        InstallationId.Value.ToString("D"),
        Id.Value,
        DisplayName,
        BaseEndpoint.AbsoluteUri,
        ProbeRelativePath,
        string.Join('\n', StaticHeaders.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"{item.Key}:{item.Value}")),
        CredentialReference.Store,
        CredentialReference.Key,
        IsEnabled,
        Version));

    private static string Hash(string value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";
}

public sealed record HttpApiConfigurationCandidate(
    HttpApiProfileId Id,
    string DisplayName,
    Uri BaseEndpoint,
    string ProbeRelativePath,
    IReadOnlyDictionary<string, string> StaticHeaders,
    bool IsEnabled);

public sealed record HttpApiProbeEvidence(
    Uri Endpoint,
    int StatusCode,
    long ResponseBytes,
    TimeSpan Duration,
    string EvidenceHash);

public sealed record HttpApiConfigurationPreview(
    InstallationId InstallationId,
    long? ExpectedVersion,
    HttpApiConfigurationCandidate Candidate,
    bool UsesNewCredential,
    string CredentialFingerprint,
    string RequestHash,
    HttpApiProbeEvidence? Probe,
    ActorId ActorId,
    CorrelationId CorrelationId);

public sealed record HttpApiConfigurationResult(
    HttpApiProfile Profile,
    string RequestHash,
    bool CredentialRotated);

public sealed record HttpApiReadRequest(
    string RelativePath,
    IReadOnlyDictionary<string, string> Query,
    int MaximumResponseBytes,
    string CorrelationId,
    string RequestId);

public sealed record HttpApiReadResponse(
    Uri Endpoint,
    int StatusCode,
    string ContentType,
    string Body,
    string EvidenceHash);
