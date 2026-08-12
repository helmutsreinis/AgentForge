using System.Text.Json;
using AgentForge.Abstractions.Devices;
using AgentForge.Domain.Devices;
using AgentForge.Domain.Primitives;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteDecoderProposalRepository(AgentForgeDbContext dbContext) : IDecoderProposalRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask<DecoderProposalSnapshot?> GetLatestAsync(
        DecoderProposalId id, CancellationToken cancellationToken) => Map(await dbContext.DecoderProposalSnapshots
            .AsNoTracking().Where(item => item.ProposalId == id.Value).OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync(cancellationToken));

    public async ValueTask<IReadOnlyList<DecoderProposalSnapshot>> ListAsync(
        DecoderProposalId id, CancellationToken cancellationToken) => (await dbContext.DecoderProposalSnapshots
            .AsNoTracking().Where(item => item.ProposalId == id.Value).OrderBy(item => item.Version)
            .ToArrayAsync(cancellationToken)).Select(item => Map(item)!).ToArray();

    public async ValueTask<string?> GetActiveHashAsync(
        InstallationId installationId, string decoderId, CancellationToken cancellationToken) =>
        (await dbContext.DecoderActiveVersions.AsNoTracking().SingleOrDefaultAsync(item =>
            item.InstallationId == installationId.Value && item.DecoderId == decoderId, cancellationToken))?.CandidateHash;

    public async ValueTask AppendAsync(
        DecoderProposalSnapshot snapshot, long? expectedVersion, CancellationToken cancellationToken)
    {
        if (!snapshot.IsConsistent()) throw new InvalidDataException("Decoder proposal snapshot is inconsistent.");
        var actual = await dbContext.DecoderProposalSnapshots.Where(item => item.ProposalId == snapshot.Id.Value)
            .MaxAsync(item => (long?)item.Version, cancellationToken);
        if (actual != expectedVersion || snapshot.Version != (expectedVersion ?? -1) + 1)
            throw new DbUpdateConcurrencyException("Decoder proposal version is stale.");
        await dbContext.DecoderProposalSnapshots.AddAsync(new DecoderProposalSnapshotEntity
        {
            ProposalId = snapshot.Id.Value,
            Version = snapshot.Version,
            InstallationId = snapshot.InstallationId.Value,
            DecoderId = snapshot.Candidate.DecoderId,
            State = snapshot.State.ToString(),
            CandidateHash = snapshot.Candidate.DefinitionHash,
            BaselineHash = snapshot.BaselineHash,
            PreviousSnapshotHash = snapshot.PreviousSnapshotHash,
            SnapshotHash = snapshot.SnapshotHash,
            UpdatedAtUtcTicks = snapshot.UpdatedAtUtc.UtcTicks,
            SnapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions),
        }, cancellationToken);
    }

    public async ValueTask SetActiveHashAsync(
        InstallationId installationId, string decoderId, string? candidateHash,
        string? expectedCurrentHash, CancellationToken cancellationToken)
    {
        var row = await dbContext.DecoderActiveVersions.SingleOrDefaultAsync(item =>
            item.InstallationId == installationId.Value && item.DecoderId == decoderId, cancellationToken);
        if (row?.CandidateHash != expectedCurrentHash)
            throw new DbUpdateConcurrencyException("Decoder active baseline is stale.");
        if (candidateHash is null)
        {
            if (row is not null) dbContext.DecoderActiveVersions.Remove(row);
            return;
        }
        if (!SerialDeviceRecordValidator.IsSha256(candidateHash))
            throw new InvalidDataException("Decoder active hash is invalid.");
        if (row is null)
        {
            await dbContext.DecoderActiveVersions.AddAsync(new DecoderActiveVersionEntity
            {
                InstallationId = installationId.Value,
                DecoderId = decoderId,
                CandidateHash = candidateHash,
                Version = 0,
            }, cancellationToken);
        }
        else
        {
            var expectedVersion = row.Version;
            row.CandidateHash = candidateHash;
            row.Version = checked(row.Version + 1);
            dbContext.Entry(row).Property(item => item.Version).OriginalValue = expectedVersion;
        }
    }

    private static DecoderProposalSnapshot? Map(DecoderProposalSnapshotEntity? entity)
    {
        if (entity is null) return null;
        var snapshot = JsonSerializer.Deserialize<DecoderProposalSnapshot>(entity.SnapshotJson, JsonOptions)
            ?? throw new InvalidDataException("Persisted decoder snapshot is empty.");
        return snapshot.IsConsistent() && snapshot.Id.Value == entity.ProposalId && snapshot.Version == entity.Version &&
            snapshot.InstallationId.Value == entity.InstallationId && snapshot.Candidate.DecoderId == entity.DecoderId &&
            snapshot.State.ToString() == entity.State && snapshot.Candidate.DefinitionHash == entity.CandidateHash &&
            snapshot.BaselineHash == entity.BaselineHash && snapshot.PreviousSnapshotHash == entity.PreviousSnapshotHash &&
            snapshot.SnapshotHash == entity.SnapshotHash && snapshot.UpdatedAtUtc.UtcTicks == entity.UpdatedAtUtcTicks
            ? snapshot : throw new InvalidDataException("Persisted decoder snapshot failed integrity validation.");
    }
}
