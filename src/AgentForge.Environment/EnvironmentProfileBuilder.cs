using System.Security.Cryptography;
using System.Text.Json;
using AgentForge.Domain.Environments;
using AgentForge.Domain.Primitives;

namespace AgentForge.Environment;

public static class EnvironmentProfileBuilder
{
    private const int MaximumManagers = 128;
    private const int MaximumAccelerators = 128;
    private const int MaximumExecutables = 20_000;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static DomainResult<EnvironmentProfile> Build(
        EnvironmentObservation observation,
        DateTimeOffset observedAt,
        ActorId actorId,
        CorrelationId correlationId)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var failure = Validate(observation, actorId, correlationId);
        if (failure is not null)
        {
            return DomainResult.Fail<EnvironmentProfile>(failure);
        }

        var distribution = observation.OperatingSystem.Distribution is null
            ? null
            : observation.OperatingSystem.Distribution with
            {
                Id = observation.OperatingSystem.Distribution.Id.Trim().ToLowerInvariant(),
                IdLike = NormalizeOptional(observation.OperatingSystem.Distribution.IdLike)?.ToLowerInvariant(),
                VersionId = NormalizeOptional(observation.OperatingSystem.Distribution.VersionId),
                VersionCodename = NormalizeOptional(observation.OperatingSystem.Distribution.VersionCodename)?.ToLowerInvariant(),
                PrettyName = NormalizeOptional(observation.OperatingSystem.Distribution.PrettyName),
                IsKali = string.Equals(
                    observation.OperatingSystem.Distribution.Id.Trim(),
                    "kali",
                    StringComparison.OrdinalIgnoreCase),
            };
        var operatingSystem = observation.OperatingSystem with
        {
            Description = observation.OperatingSystem.Description.Trim(),
            KernelVersion = observation.OperatingSystem.KernelVersion.Trim(),
            Distribution = distribution,
        };
        var managers = observation.Managers
            .Select(item => item with
            {
                Id = item.Id.Trim().ToLowerInvariant(),
                Path = NormalizeOptional(item.Path),
                EvidenceSource = item.EvidenceSource.Trim(),
            })
            .DistinctBy(item => (item.Kind, item.Id, item.Path), ManagerKeyComparer.Instance)
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();
        var accelerators = observation.Accelerators
            .Select(item => item with
            {
                Vendor = item.Vendor.Trim(),
                DeviceName = NormalizeOptional(item.DeviceName),
                EvidenceSource = item.EvidenceSource.Trim(),
            })
            .Distinct()
            .OrderBy(item => item.Vendor, StringComparer.Ordinal)
            .ThenBy(item => item.DeviceName, StringComparer.Ordinal)
            .ToArray();
        var pathComparer = operatingSystem.Family is HostOperatingSystem.Windows
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var executables = observation.Executables
            .Select(item => item with
            {
                Name = item.Name.Trim(),
                FullPath = item.FullPath.Trim(),
                LinkTarget = NormalizeOptional(item.LinkTarget),
                Provenance = item.Provenance.Trim(),
            })
            .DistinctBy(item => item.FullPath, pathComparer)
            .OrderBy(item => item.FullPath, pathComparer)
            .ToArray();
        var normalized = observation with
        {
            OperatingSystem = operatingSystem,
            FrameworkDescription = observation.FrameworkDescription.Trim(),
            Wsl = observation.Wsl with
            {
                DistributionName = NormalizeOptional(observation.Wsl.DistributionName),
                EvidenceSource = observation.Wsl.EvidenceSource.Trim(),
            },
            Isolation = observation.Isolation with
            {
                EvidenceSource = observation.Isolation.EvidenceSource.Trim(),
                ProductHint = NormalizeOptional(observation.Isolation.ProductHint),
            },
            FileSystem = observation.FileSystem with
            {
                CurrentRoot = observation.FileSystem.CurrentRoot.Trim(),
                TemporaryRoot = observation.FileSystem.TemporaryRoot.Trim(),
                Format = NormalizeOptional(observation.FileSystem.Format),
                EvidenceSource = observation.FileSystem.EvidenceSource.Trim(),
            },
            Privilege = observation.Privilege with
            {
                EvidenceSource = observation.Privilege.EvidenceSource.Trim(),
            },
            Managers = managers,
            Accelerators = accelerators,
            Executables = executables,
        };
        var fingerprint = ComputeFingerprint(normalized);
        return DomainResult.Success(new EnvironmentProfile(
            1,
            observedAt,
            actorId,
            correlationId,
            normalized.OperatingSystem,
            normalized.FrameworkDescription,
            normalized.ProcessorCount,
            normalized.Wsl,
            normalized.Isolation,
            normalized.FileSystem,
            normalized.Privilege,
            managers,
            accelerators,
            executables,
            normalized.ExecutableInventoryTruncated,
            fingerprint));
    }

    private static DomainFailure? Validate(
        EnvironmentObservation observation,
        ActorId actorId,
        CorrelationId correlationId)
    {
        if (!IsBounded(actorId.Value, 256) || !IsBounded(correlationId.Value, 128))
        {
            return Invalid("Actor and correlation IDs must be bounded printable values.");
        }

        if (observation.OperatingSystem is null || observation.Wsl is null || observation.Isolation is null ||
            observation.FileSystem is null || observation.Privilege is null || observation.Managers is null ||
            observation.Accelerators is null || observation.Executables is null ||
            !IsBounded(observation.OperatingSystem.Description, 512) ||
            !IsBounded(observation.OperatingSystem.KernelVersion, 256) ||
            !IsBounded(observation.FrameworkDescription, 256) ||
            !IsBounded(observation.Wsl.EvidenceSource, 128) ||
            !IsBounded(observation.Isolation.EvidenceSource, 128) ||
            !IsBounded(observation.FileSystem.CurrentRoot, 1024) ||
            !IsBounded(observation.FileSystem.TemporaryRoot, 1024) ||
            !IsBounded(observation.FileSystem.EvidenceSource, 128) ||
            !IsBounded(observation.Privilege.EvidenceSource, 128) ||
            observation.ProcessorCount is < 1 or > 1_048_576 ||
            observation.Managers.Count > MaximumManagers ||
            observation.Accelerators.Count > MaximumAccelerators ||
            observation.Executables.Count > MaximumExecutables)
        {
            return Invalid("Environment observation is missing required bounded evidence.");
        }

        if (observation.OperatingSystem.Family is HostOperatingSystem.Linux &&
            observation.OperatingSystem.Distribution is null)
        {
            return Invalid("Linux observations require distribution metadata, even when fields are unknown.");
        }

        if (observation.OperatingSystem.Distribution is { } distribution &&
            (!IsBounded(distribution.Id, 128) || !IsOptionalBounded(distribution.IdLike, 256) ||
             !IsOptionalBounded(distribution.VersionId, 128) || !IsOptionalBounded(distribution.VersionCodename, 128) ||
             !IsOptionalBounded(distribution.PrettyName, 512)))
        {
            return Invalid("Distribution metadata is invalid or oversized.");
        }

        if (observation.Managers.Any(item => item is null || !IsBounded(item.Id, 128) ||
            !IsOptionalBounded(item.Path, 1024) || !IsBounded(item.EvidenceSource, 128)) ||
            observation.Accelerators.Any(item => item is null || !IsBounded(item.Vendor, 128) ||
            !IsOptionalBounded(item.DeviceName, 512) || !IsBounded(item.EvidenceSource, 128)) ||
            observation.Executables.Any(item => item is null || !IsBounded(item.Name, 512) ||
            !IsBounded(item.FullPath, 2048) || item.Length < 0 || !IsOptionalBounded(item.LinkTarget, 2048) ||
            !IsBounded(item.Provenance, 128)))
        {
            return Invalid("Environment inventory contains invalid or oversized entries.");
        }

        return null;
    }

    private static string ComputeFingerprint(EnvironmentObservation observation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1,
            observation.OperatingSystem,
            observation.FrameworkDescription,
            observation.ProcessorCount,
            observation.Wsl,
            observation.Isolation,
            observation.FileSystem,
            observation.Privilege,
            observation.Managers,
            observation.Accelerators,
            observation.Executables,
            observation.ExecutableInventoryTruncated,
        }, SerializerOptions);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    }

    private static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);

    private static bool IsOptionalBounded(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) || value.Length <= maximumLength && !value.Any(char.IsControl);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DomainFailure Invalid(string message) =>
        new(FailureCode.ValidationFailure, message);

    private sealed class ManagerKeyComparer : IEqualityComparer<(EnvironmentManagerKind Kind, string Id, string? Path)>
    {
        public static ManagerKeyComparer Instance { get; } = new();

        public bool Equals(
            (EnvironmentManagerKind Kind, string Id, string? Path) x,
            (EnvironmentManagerKind Kind, string Id, string? Path) y) =>
            x.Kind == y.Kind &&
            string.Equals(x.Id, y.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Path, y.Path, StringComparison.Ordinal);

        public int GetHashCode((EnvironmentManagerKind Kind, string Id, string? Path) obj) =>
            HashCode.Combine(obj.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Id), obj.Path);
    }
}
