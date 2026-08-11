using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using AgentForge.Abstractions.Models;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;

namespace AgentForge.Models;

public sealed class ModelProviderHealthCatalog : IModelProviderHealthSource
{
    private const int MaximumEvidenceRecords = 256;
    private static readonly TimeSpan MaximumEvidenceLifetime = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyList<ModelProviderHealthEvidence> _evidence;

    private ModelProviderHealthCatalog(IReadOnlyList<ModelProviderHealthEvidence> evidence)
    {
        _evidence = evidence;
        EvidenceHash = ComputeEvidenceHash(evidence);
    }

    public string EvidenceHash { get; }

    public IReadOnlyList<ModelProviderHealthEvidence> List() => _evidence;

    public static DomainResult<ModelProviderHealthCatalog> Create(
        IEnumerable<ModelProviderHealthEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var normalized = new List<ModelProviderHealthEvidence>();
        var profileIds = new HashSet<AgentForge.Domain.Providers.ProviderProfileId>();
        try
        {
            foreach (var item in evidence)
            {
                if (normalized.Count >= MaximumEvidenceRecords || !Validate(item) ||
                    !profileIds.Add(item.ProfileId))
                {
                    return Invalid();
                }

                normalized.Add(item with { });
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Invalid();
        }

        var ordered = normalized
            .OrderBy(item => item.ProfileId.Value)
            .ToArray();
        return DomainResult.Success(new ModelProviderHealthCatalog(
            new ReadOnlyCollection<ModelProviderHealthEvidence>(ordered)));
    }

    public ValueTask<DomainResult<IReadOnlyList<ModelProviderHealthEvidence>>> ReadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DomainResult.Success(_evidence));
    }

    internal static string ComputeEvidenceHash(IEnumerable<ModelProviderHealthEvidence> evidence)
    {
        var canonical = evidence
            .OrderBy(item => item.ProfileId.Value)
            .Select(item => new
            {
                ProfileId = item.ProfileId.ToString(),
                Status = item.Status.ToString(),
                Source = item.Source.ToString(),
                item.ConsecutiveFailures,
                item.EvidenceCode,
                item.ObservedAt,
                item.ExpiresAt,
                item.RetryAfter,
            })
            .ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, SerializerOptions);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    }

    private static bool Validate(ModelProviderHealthEvidence evidence) =>
        evidence is not null && evidence.ProfileId.Value != Guid.Empty &&
        Enum.IsDefined(evidence.Status) && Enum.IsDefined(evidence.Source) &&
        evidence.ConsecutiveFailures is >= 0 and <= 1_000 &&
        IsEvidenceCode(evidence.EvidenceCode) && evidence.ObservedAt != default &&
        evidence.ExpiresAt > evidence.ObservedAt &&
        evidence.ExpiresAt - evidence.ObservedAt <= MaximumEvidenceLifetime &&
        (evidence.RetryAfter is null ||
            evidence.RetryAfter > evidence.ObservedAt && evidence.RetryAfter <= evidence.ExpiresAt) &&
        (evidence.Status is ModelProviderHealthStatus.Healthy
            ? evidence.ConsecutiveFailures == 0 && evidence.RetryAfter is null
            : evidence.Status is ModelProviderHealthStatus.TemporarilyUnavailable
                ? evidence.ConsecutiveFailures > 0 && evidence.RetryAfter is not null
                : evidence.RetryAfter is null);

    private static bool IsEvidenceCode(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');

    private static DomainResult<ModelProviderHealthCatalog> Invalid() =>
        DomainResult.Fail<ModelProviderHealthCatalog>(new DomainFailure(
            FailureCode.ValidationFailure,
            "Model provider health evidence is invalid, duplicate, or unbounded."));
}
