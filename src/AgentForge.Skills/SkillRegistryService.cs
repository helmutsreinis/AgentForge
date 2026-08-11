using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Skills;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Skills;

namespace AgentForge.Skills;

internal sealed class SkillRegistryService(
    ISkillPackageLoader loader,
    ISkillRegistryRepository repository,
    IArtifactStore artifacts,
    IAuditRecorder audit,
    IUnitOfWork unitOfWork,
    IClock clock) : ISkillRegistryService
{
    public async Task<DomainResult<SkillInstallResult>> InstallAsync(
        InstallationId installationId,
        string packageDirectory,
        SkillPackageProvenance provenance,
        ActorId actorId,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        if (installationId.Value == Guid.Empty || !Enum.IsDefined(provenance))
        {
            return Invalid<SkillInstallResult>("Skill installation authority is invalid.");
        }

        var loaded = await loader.LoadAsync(packageDirectory, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return DomainResult.Fail<SkillInstallResult>(loaded.Failure!);
        }

        var existing = await repository.FindAsync(
            installationId,
            loaded.Value.Package.Id,
            loaded.Value.Package.Version,
            cancellationToken);
        if (existing is not null)
        {
            return string.Equals(existing.Package.PackageHash, loaded.Value.Package.PackageHash, StringComparison.Ordinal)
                ? DomainResult.Success(new SkillInstallResult(existing, true))
                : DomainResult.Fail<SkillInstallResult>(new DomainFailure(
                    FailureCode.ConcurrencyConflict,
                    "The immutable skill version already exists with different content."));
        }

        var catalog = await repository.ListAsync(installationId, cancellationToken);
        if (!DependenciesAreValid(catalog, loaded.Value.Package))
        {
            return Invalid<SkillInstallResult>("Skill dependencies are missing, cyclic, archived, or quarantined.");
        }

        await using var content = new MemoryStream(loaded.Value.CanonicalBytes.ToArray(), writable: false);
        var artifact = await artifacts.PutAsync(
            content,
            "application/vnd.agentforge.skill-package",
            cancellationToken);
        if (!string.Equals(artifact.ContentHash, loaded.Value.Package.PackageHash, StringComparison.Ordinal))
        {
            return DomainResult.Fail<SkillInstallResult>(new DomainFailure(
                FailureCode.ValidationFailure,
                "The stored skill artifact did not match its validated package hash."));
        }

        var now = clock.UtcNow;
        var package = loaded.Value.Package;
        var registered = new RegisteredSkillVersion(
            installationId,
            new SkillPackageDescriptor(
                package.Id,
                package.Version,
                package.Description,
                package.Dependencies.Select(item => item with { }).ToArray(),
                package.Requirements with
                {
                    OperatingSystems = package.Requirements.OperatingSystems.ToArray(),
                    ModelCapabilities = package.Requirements.ModelCapabilities.ToArray(),
                    ToolIds = package.Requirements.ToolIds.ToArray(),
                },
                package.Permissions.ToArray(),
                package.ManifestHash,
                package.PackageHash,
                package.Signature),
            artifact,
            SkillPackageStatus.Installed,
            provenance,
            0,
            now,
            now,
            actorId,
            correlationId);
        if (!SkillGovernanceStateMachine.IsValid(registered))
        {
            return Invalid<SkillInstallResult>("The validated skill could not form an immutable registry record.");
        }

        await repository.AddAsync(registered, cancellationToken);
        await RecordAsync(
            registered,
            "skills.package-installed",
            new { registered.Package.PackageHash, registered.Package.ManifestHash, Provenance = provenance.ToString() },
            cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded
            ? DomainResult.Success(new SkillInstallResult(registered, false))
            : DomainResult.Fail<SkillInstallResult>(commit.Failure!);
    }

    public async Task<DomainResult<RegisteredSkillVersion>> SetStatusAsync(
        InstallationId installationId,
        SkillId skillId,
        SkillVersion version,
        long expectedRecordVersion,
        SkillPackageStatus status,
        ActorId actorId,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        if (status is SkillPackageStatus.Active)
        {
            return Invalid<RegisteredSkillVersion>("Active status is controlled only by governed promotion.");
        }

        var current = await repository.FindAsync(installationId, skillId, version, cancellationToken);
        if (current is null)
        {
            return Invalid<RegisteredSkillVersion>("The skill version does not exist.");
        }

        if (current.RecordVersion != expectedRecordVersion || current.Status is SkillPackageStatus.Active ||
            current.Status == status || current.Status is SkillPackageStatus.Archived &&
                status is not SkillPackageStatus.Installed)
        {
            return DomainResult.Fail<RegisteredSkillVersion>(new DomainFailure(
                FailureCode.ConcurrencyConflict,
                "The skill status request is stale or not an allowed archive/restore/quarantine transition."));
        }

        var updated = current with
        {
            Status = status,
            RecordVersion = current.RecordVersion + 1,
            UpdatedAt = clock.UtcNow,
            ActorId = actorId,
            CorrelationId = correlationId,
        };
        await repository.UpdateAsync(updated, expectedRecordVersion, cancellationToken);
        await RecordAsync(updated, "skills.package-status-changed", new { Status = status.ToString() }, cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded ? DomainResult.Success(updated) : DomainResult.Fail<RegisteredSkillVersion>(commit.Failure!);
    }

    public async Task<DomainResult<IReadOnlyList<SkillSearchResult>>> SearchAsync(
        InstallationId installationId,
        string query,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        if (installationId.Value == Guid.Empty || string.IsNullOrWhiteSpace(query) || query.Length > 256 ||
            query.Any(char.IsControl) || maximumResults is < 1 or > 64)
        {
            return Invalid<IReadOnlyList<SkillSearchResult>>("Skill search input is invalid.");
        }

        var normalized = query.Trim();
        var matches = (await repository.ListAsync(installationId, cancellationToken))
            .Where(item => item.Status is not (SkillPackageStatus.Quarantined or SkillPackageStatus.Archived) &&
                (item.Package.Id.Value.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                    item.Package.Description.Contains(normalized, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(item => item.Status is SkillPackageStatus.Active)
            .ThenBy(item => item.Package.Id.Value, StringComparer.Ordinal)
            .ThenByDescending(item => item.Package.Version)
            .Take(maximumResults)
            .Select(item => new SkillSearchResult(
                item.Package.Id,
                item.Package.Version,
                item.Package.Description,
                item.Status,
                item.Provenance))
            .ToArray();
        return DomainResult.Success<IReadOnlyList<SkillSearchResult>>(matches);
    }

    private static bool DependenciesAreValid(
        IReadOnlyList<RegisteredSkillVersion> catalog,
        SkillPackage candidate)
    {
        var exact = catalog.Where(item => item.Status is not (SkillPackageStatus.Archived or SkillPackageStatus.Quarantined))
            .ToDictionary(item => (item.Package.Id, item.Package.Version));
        if (candidate.Dependencies.Any(dependency => !exact.ContainsKey((dependency.Id, dependency.Version))))
        {
            return false;
        }

        var candidateKey = (candidate.Id, candidate.Version);
        var dependencies = exact.ToDictionary(
            item => item.Key,
            item => item.Value.Package.Dependencies.Select(dependency => (dependency.Id, dependency.Version)).ToArray());
        dependencies[candidateKey] = candidate.Dependencies.Select(dependency => (dependency.Id, dependency.Version)).ToArray();
        var visiting = new HashSet<(SkillId, SkillVersion)>();
        var visited = new HashSet<(SkillId, SkillVersion)>();
        bool Visit((SkillId, SkillVersion) key)
        {
            if (visiting.Contains(key))
            {
                return true;
            }

            if (!visited.Add(key))
            {
                return false;
            }

            visiting.Add(key);
            var cycle = dependencies[key].Any(Visit);
            visiting.Remove(key);
            return cycle;
        }

        return !Visit(candidateKey);
    }

    private async Task RecordAsync(
        RegisteredSkillVersion version,
        string operation,
        object output,
        CancellationToken cancellationToken) => await audit.RecordAsync(new AuditRecordRequest(
        version.InstallationId,
        version.ActorId,
        version.CorrelationId,
        null,
        operation,
        AuditOutcome.Succeeded,
        new
        {
            SkillId = version.Package.Id.Value,
            Version = version.Package.Version.Value,
            version.RecordVersion,
        },
        output,
        null), cancellationToken);

    private static DomainResult<T> Invalid<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ValidationFailure, message));
}
