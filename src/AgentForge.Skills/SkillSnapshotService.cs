using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Skills;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Skills;

namespace AgentForge.Skills;

internal sealed class SkillSnapshotService(
    ISkillRegistryRepository registry,
    ISkillRunSnapshotStore snapshots,
    IArtifactStore artifacts,
    IAuditRecorder audit,
    IUnitOfWork unitOfWork,
    IClock clock) : ISkillSnapshotService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<DomainResult<SkillRunSnapshot>> CreateAsync(
        SkillRunSnapshotId snapshotId,
        InstallationId installationId,
        IReadOnlyList<SkillId> selectedSkillIds,
        ActorId actorId,
        string idempotencyKey,
        CorrelationId correlationId,
        CorrelationId? causationId,
        CancellationToken cancellationToken)
    {
        var replay = await snapshots.FindByIdempotencyKeyAsync(installationId, idempotencyKey, cancellationToken);
        if (replay is not null)
        {
            return replay.Id == snapshotId && replay.ActorId == actorId && replay.CorrelationId == correlationId
                ? DomainResult.Success(replay)
                : Conflict("The skill snapshot idempotency key is already bound to another request.");
        }

        if (selectedSkillIds is null || selectedSkillIds.Count is < 1 or > 128 ||
            selectedSkillIds.Distinct().Count() != selectedSkillIds.Count)
        {
            return Invalid<SkillRunSnapshot>("A bounded distinct set of active skills is required.");
        }

        var selected = new Dictionary<SkillId, RegisteredSkillVersion>();
        foreach (var skillId in selectedSkillIds)
        {
            var active = await registry.FindActiveAsync(installationId, skillId, cancellationToken);
            if (active is null || !await AddDependenciesAsync(active, selected, cancellationToken))
            {
                return Invalid<SkillRunSnapshot>("An active skill or one of its exact dependencies is unavailable.");
            }
        }

        var created = SkillGovernanceStateMachine.CreateRunSnapshot(
            snapshotId,
            installationId,
            selected.Values.ToArray(),
            actorId,
            idempotencyKey,
            correlationId,
            causationId,
            clock.UtcNow);
        if (!created.IsSuccess)
        {
            return created;
        }

        await snapshots.AddAsync(created.Value, cancellationToken);
        await audit.RecordAsync(new AuditRecordRequest(
            installationId,
            actorId,
            correlationId,
            causationId,
            "skills.run-snapshot-created",
            AuditOutcome.Succeeded,
            new { SnapshotId = snapshotId.ToString(), SkillIds = selectedSkillIds.Select(item => item.Value) },
            new { created.Value.SnapshotHash, SelectionCount = created.Value.Selections.Count },
            null), cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded
            ? created
            : DomainResult.Fail<SkillRunSnapshot>(commit.Failure!);
    }

    public async Task<DomainResult<string>> OpenBodyAsync(
        SkillRunSnapshotId snapshotId,
        SkillId skillId,
        CancellationToken cancellationToken)
    {
        var snapshot = await snapshots.FindAsync(snapshotId, cancellationToken);
        var selection = snapshot?.Selections.SingleOrDefault(item => item.SkillId == skillId);
        if (snapshot is null || selection is null || !SkillGovernanceStateMachine.IsConsistent(snapshot))
        {
            return DomainResult.Fail<string>(new DomainFailure(
                FailureCode.PolicyDenied,
                "Skill content is available only through an exact valid run snapshot."));
        }

        await using var input = await artifacts.OpenReadAsync(selection.Artifact, cancellationToken);
        using var output = new MemoryStream((int)selection.Artifact.Length);
        await input.CopyToAsync(output, cancellationToken);
        var bytes = output.ToArray();
        var hash = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
        if (bytes.Length != selection.Artifact.Length || hash != selection.PackageHash)
        {
            return Invalid<string>("The immutable skill artifact failed integrity validation.");
        }

        return TryReadMarkdown(bytes, out var markdown)
            ? DomainResult.Success(markdown!)
            : Invalid<string>("The immutable skill artifact bundle is malformed.");
    }

    private async Task<bool> AddDependenciesAsync(
        RegisteredSkillVersion current,
        Dictionary<SkillId, RegisteredSkillVersion> selected,
        CancellationToken cancellationToken)
    {
        if (current.Status is SkillPackageStatus.Archived or SkillPackageStatus.Quarantined)
        {
            return false;
        }

        if (selected.TryGetValue(current.Package.Id, out var existing))
        {
            return existing.Package.Version == current.Package.Version;
        }

        selected.Add(current.Package.Id, current);
        foreach (var dependency in current.Package.Dependencies)
        {
            var exact = await registry.FindAsync(
                current.InstallationId,
                dependency.Id,
                dependency.Version,
                cancellationToken);
            if (exact is null || !await AddDependenciesAsync(exact, selected, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadMarkdown(byte[] bundle, out string? markdown)
    {
        markdown = null;
        var offset = 0;
        try
        {
            while (offset < bundle.Length)
            {
                if (bundle.Length - offset < 4)
                {
                    return false;
                }

                var pathLength = BinaryPrimitives.ReadInt32BigEndian(bundle.AsSpan(offset, 4));
                offset += 4;
                if (pathLength is < 1 or > 512 || bundle.Length - offset < pathLength + 4)
                {
                    return false;
                }

                var path = StrictUtf8.GetString(bundle, offset, pathLength);
                offset += pathLength;
                var fileLength = BinaryPrimitives.ReadInt32BigEndian(bundle.AsSpan(offset, 4));
                offset += 4;
                if (fileLength is < 0 or > 1_048_576 || bundle.Length - offset < fileLength)
                {
                    return false;
                }

                if (path == "SKILL.md")
                {
                    markdown = StrictUtf8.GetString(bundle, offset, fileLength);
                }

                offset += fileLength;
            }
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        return markdown is not null;
    }

    private static DomainResult<T> Invalid<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<SkillRunSnapshot> Conflict(string message) =>
        DomainResult.Fail<SkillRunSnapshot>(new DomainFailure(FailureCode.ConcurrencyConflict, message));
}
