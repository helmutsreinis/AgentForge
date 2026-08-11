using System.Text.Json;
using AgentForge.Abstractions.Skills;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Skills;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteSkillRegistryRepository(AgentForgeDbContext dbContext) : ISkillRegistryRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask AddAsync(RegisteredSkillVersion version, CancellationToken cancellationToken)
    {
        if (!SkillGovernanceStateMachine.IsValid(version))
        {
            throw new ArgumentException("Only a valid skill version can be persisted.", nameof(version));
        }

        await dbContext.SkillVersions.AddAsync(Map(version), cancellationToken);
    }

    public async ValueTask UpdateAsync(
        RegisteredSkillVersion version,
        long expectedRecordVersion,
        CancellationToken cancellationToken)
    {
        if (!SkillGovernanceStateMachine.IsValid(version))
        {
            throw new ArgumentException("Only a valid skill version can be persisted.", nameof(version));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var key = (version.InstallationId.Value, version.Package.Id.Value, version.Package.Version.Value);
        var entity = dbContext.ChangeTracker.Entries<SkillVersionEntity>().FirstOrDefault(item =>
            item.Entity.InstallationId == key.Item1 && item.Entity.SkillId == key.Item2 &&
            item.Entity.Version == key.Item3)?.Entity;
        if (entity is null)
        {
            entity = Map(version);
            dbContext.SkillVersions.Attach(entity);
        }
        else
        {
            dbContext.Entry(entity).CurrentValues.SetValues(Map(version));
        }

        var entry = dbContext.Entry(entity);
        entry.State = EntityState.Modified;
        entry.Property(item => item.RecordVersion).OriginalValue = expectedRecordVersion;
        if (version.Status is SkillPackageStatus.Active)
        {
            var pointer = dbContext.ChangeTracker.Entries<SkillActiveVersionEntity>().FirstOrDefault(item =>
                item.Entity.InstallationId == version.InstallationId.Value &&
                item.Entity.SkillId == version.Package.Id.Value)?.Entity;
            pointer ??= await dbContext.SkillActiveVersions.SingleOrDefaultAsync(item =>
                item.InstallationId == version.InstallationId.Value &&
                item.SkillId == version.Package.Id.Value, cancellationToken);
            if (pointer is null)
            {
                await dbContext.SkillActiveVersions.AddAsync(new SkillActiveVersionEntity
                {
                    InstallationId = version.InstallationId.Value,
                    SkillId = version.Package.Id.Value,
                    Version = version.Package.Version.Value,
                }, cancellationToken);
            }
            else
            {
                pointer.Version = version.Package.Version.Value;
            }
        }
    }

    public async ValueTask<RegisteredSkillVersion?> FindAsync(
        InstallationId installationId,
        SkillId skillId,
        SkillVersion version,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.SkillVersions.AsNoTracking().SingleOrDefaultAsync(item =>
            item.InstallationId == installationId.Value && item.SkillId == skillId.Value &&
            item.Version == version.Value, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async ValueTask<RegisteredSkillVersion?> FindActiveAsync(
        InstallationId installationId,
        SkillId skillId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.SkillActiveVersions.AsNoTracking()
            .Where(pointer => pointer.InstallationId == installationId.Value && pointer.SkillId == skillId.Value)
            .Join(dbContext.SkillVersions.AsNoTracking(),
                pointer => new { pointer.InstallationId, pointer.SkillId, pointer.Version },
                version => new { version.InstallationId, version.SkillId, version.Version },
                (_, version) => version)
            .SingleOrDefaultAsync(cancellationToken);
        if (entity is not null && entity.Status != nameof(SkillPackageStatus.Active))
        {
            throw new InvalidOperationException("The active skill pointer referenced a non-active version.");
        }
        return entity is null ? null : Map(entity);
    }

    public async ValueTask<IReadOnlyList<RegisteredSkillVersion>> ListAsync(
        InstallationId installationId,
        CancellationToken cancellationToken) => (await dbContext.SkillVersions.AsNoTracking()
        .Where(item => item.InstallationId == installationId.Value)
        .OrderBy(item => item.SkillId).ThenBy(item => item.Version)
        .ToArrayAsync(cancellationToken)).Select(Map).ToArray();

    private static SkillVersionEntity Map(RegisteredSkillVersion version) => new()
    {
        InstallationId = version.InstallationId.Value,
        SkillId = version.Package.Id.Value,
        Version = version.Package.Version.Value,
        ArtifactContentHash = version.Artifact.ContentHash,
        PackageHash = version.Package.PackageHash,
        ManifestHash = version.Package.ManifestHash,
        Status = version.Status.ToString(),
        Provenance = version.Provenance.ToString(),
        DescriptorJson = JsonSerializer.Serialize(version, SerializerOptions),
        RecordVersion = version.RecordVersion,
        CreatedAtUtcTicks = version.CreatedAt.UtcTicks,
        UpdatedAtUtcTicks = version.UpdatedAt.UtcTicks,
        ActorId = version.ActorId.Value,
        CorrelationId = version.CorrelationId.Value,
    };

    private static RegisteredSkillVersion Map(SkillVersionEntity entity)
    {
        var version = JsonSerializer.Deserialize<RegisteredSkillVersion>(entity.DescriptorJson, SerializerOptions)
            ?? throw new InvalidOperationException("The persisted skill version was empty.");
        if (version.InstallationId.Value != entity.InstallationId || version.Package.Id.Value != entity.SkillId ||
            version.Package.Version.Value != entity.Version || version.Artifact.ContentHash != entity.ArtifactContentHash ||
            version.Package.PackageHash != entity.PackageHash || version.Package.ManifestHash != entity.ManifestHash ||
            version.Status.ToString() != entity.Status || version.Provenance.ToString() != entity.Provenance ||
            version.RecordVersion != entity.RecordVersion || version.CreatedAt.UtcTicks != entity.CreatedAtUtcTicks ||
            version.UpdatedAt.UtcTicks != entity.UpdatedAtUtcTicks || version.ActorId.Value != entity.ActorId ||
            version.CorrelationId.Value != entity.CorrelationId || !SkillGovernanceStateMachine.IsValid(version))
        {
            throw new InvalidOperationException("The persisted skill version failed integrity validation.");
        }

        return version;
    }
}
