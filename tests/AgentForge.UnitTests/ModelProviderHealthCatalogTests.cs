using AgentForge.Domain.Models;
using AgentForge.Domain.Providers;
using AgentForge.Models;

namespace AgentForge.UnitTests;

public sealed class ModelProviderHealthCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 16, 0, 0, TimeSpan.Zero);
    private static readonly ProviderProfileId ProfileId = new(
        Guid.Parse("fd053154-6647-4879-9587-b5542a47911f"));

    [Fact]
    public void Snapshots_mutable_input_and_hashes_every_health_field()
    {
        var source = new List<ModelProviderHealthEvidence> { Healthy() };
        var first = ModelProviderHealthCatalog.Create(source);
        source[0] = Healthy() with { EvidenceCode = "changed" };
        var second = ModelProviderHealthCatalog.Create(source);

        Assert.True(first.IsSuccess, first.Failure?.Message);
        Assert.True(second.IsSuccess, second.Failure?.Message);
        Assert.Equal("probe-ok", first.Value.List()[0].EvidenceCode);
        Assert.NotEqual(first.Value.EvidenceHash, second.Value.EvidenceHash);
        Assert.Equal(71, first.Value.EvidenceHash.Length);
    }

    [Fact]
    public void Duplicate_invalid_and_unbounded_evidence_fail_closed()
    {
        var duplicate = ModelProviderHealthCatalog.Create([Healthy(), Healthy()]);
        var invalid = ModelProviderHealthCatalog.Create([
            Healthy() with { EvidenceCode = "remote body: not safe" },
        ]);
        var excessiveLifetime = ModelProviderHealthCatalog.Create([
            Healthy() with { ExpiresAt = Now.AddHours(1) },
        ]);
        var unbounded = ModelProviderHealthCatalog.Create(
            Enumerable.Range(0, 257).Select(index => Healthy() with
            {
                ProfileId = new ProviderProfileId(GuidFromIndex(index)),
            }));

        Assert.Equal(AgentForge.Domain.Primitives.FailureCode.ValidationFailure, duplicate.Failure?.Code);
        Assert.Equal(AgentForge.Domain.Primitives.FailureCode.ValidationFailure, invalid.Failure?.Code);
        Assert.Equal(
            AgentForge.Domain.Primitives.FailureCode.ValidationFailure,
            excessiveLifetime.Failure?.Code);
        Assert.Equal(AgentForge.Domain.Primitives.FailureCode.ValidationFailure, unbounded.Failure?.Code);
    }

    [Fact]
    public void Temporary_failure_requires_bounded_retry_evidence()
    {
        var noFailures = ModelProviderHealthCatalog.Create([
            Healthy() with
            {
                Status = ModelProviderHealthStatus.TemporarilyUnavailable,
                ConsecutiveFailures = 0,
                RetryAfter = Now.AddSeconds(10),
            },
        ]);
        var noRetry = ModelProviderHealthCatalog.Create([
            Healthy() with
            {
                Status = ModelProviderHealthStatus.TemporarilyUnavailable,
                ConsecutiveFailures = 1,
            },
        ]);
        var valid = ModelProviderHealthCatalog.Create([
            Healthy() with
            {
                Status = ModelProviderHealthStatus.TemporarilyUnavailable,
                Source = ModelHealthEvidenceSource.Observed,
                ConsecutiveFailures = 1,
                EvidenceCode = "timeout",
                RetryAfter = Now.AddSeconds(10),
            },
        ]);

        Assert.False(noFailures.IsSuccess);
        Assert.False(noRetry.IsSuccess);
        Assert.True(valid.IsSuccess, valid.Failure?.Message);
    }

    private static ModelProviderHealthEvidence Healthy() => new(
        ProfileId,
        ModelProviderHealthStatus.Healthy,
        ModelHealthEvidenceSource.Probed,
        0,
        "probe-ok",
        Now.AddMinutes(-1),
        Now.AddMinutes(1));

    private static Guid GuidFromIndex(int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, index + 1);
        return new Guid(bytes);
    }
}
