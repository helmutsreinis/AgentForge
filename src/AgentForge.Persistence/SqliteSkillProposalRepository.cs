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
