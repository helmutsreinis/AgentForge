using System.Text.Json;
using AgentForge.Abstractions.Skills;
using AgentForge.Domain.Skills;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteSkillProposalRepository(AgentForgeDbContext dbContext) : ISkillProposalRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask AppendAsync(SkillProposal proposal, CancellationToken cancellationToken)
    {
        if (!SkillGovernanceStateMachine.IsConsistent(proposal))
        {
            throw new ArgumentException("Only a consistent skill proposal can be persisted.", nameof(proposal));
        }

        await dbContext.SkillProposalSnapshots.AddAsync(new SkillProposalSnapshotEntity
        {
            ProposalId = proposal.Id.Value,
            Version = proposal.Version,
            InstallationId = proposal.InstallationId.Value,
            SkillId = proposal.SkillId.Value,
            State = proposal.State.ToString(),
            PreviousSnapshotHash = proposal.PreviousSnapshotHash,
            SnapshotHash = proposal.SnapshotHash,
            ProposalJson = JsonSerializer.Serialize(proposal, SerializerOptions),
            UpdatedAtUtcTicks = proposal.UpdatedAt.UtcTicks,
            CorrelationId = proposal.CorrelationId.Value,
        }, cancellationToken);
    }

    public async ValueTask<SkillProposal?> FindLatestAsync(
        SkillProposalId proposalId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.SkillProposalSnapshots.AsNoTracking()
            .Where(item => item.ProposalId == proposalId.Value)
            .OrderByDescending(item => item.Version).FirstOrDefaultAsync(cancellationToken);
        if (entity is null)
        {
            return null;
        }

        return Map(entity);
    }

    public async ValueTask<IReadOnlyList<SkillProposal>> ListLatestAsync(
        Domain.Primitives.InstallationId installationId,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        if (maximumResults is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        }

        var latest = dbContext.SkillProposalSnapshots.AsNoTracking()
            .Where(item => item.InstallationId == installationId.Value)
            .GroupBy(item => item.ProposalId)
            .Select(group => new { ProposalId = group.Key, Version = group.Max(item => item.Version) });
        var entities = await dbContext.SkillProposalSnapshots.AsNoTracking()
            .Join(
                latest,
                item => new { item.ProposalId, item.Version },
                item => new { item.ProposalId, item.Version },
                (snapshot, _) => snapshot)
            .OrderByDescending(item => item.UpdatedAtUtcTicks)
            .Take(maximumResults)
            .ToArrayAsync(cancellationToken);
        return entities.Select(Map).ToArray();
    }

    private static SkillProposal Map(SkillProposalSnapshotEntity entity)
    {
        var proposal = JsonSerializer.Deserialize<SkillProposal>(entity.ProposalJson, SerializerOptions)
            ?? throw new InvalidOperationException("The persisted skill proposal was empty.");
        if (proposal.Id.Value != entity.ProposalId || proposal.Version != entity.Version ||
            proposal.InstallationId.Value != entity.InstallationId || proposal.SkillId.Value != entity.SkillId ||
            proposal.State.ToString() != entity.State || proposal.PreviousSnapshotHash != entity.PreviousSnapshotHash ||
            proposal.SnapshotHash != entity.SnapshotHash || proposal.UpdatedAt.UtcTicks != entity.UpdatedAtUtcTicks ||
            proposal.CorrelationId.Value != entity.CorrelationId || !SkillGovernanceStateMachine.IsConsistent(proposal))
        {
            throw new InvalidOperationException("The persisted skill proposal failed integrity validation.");
        }

        return proposal;
    }
}
