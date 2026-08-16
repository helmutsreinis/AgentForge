using System.Text.Json;
using AgentForge.Abstractions.Learning;
using AgentForge.Domain.Learning;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Skills;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteLearningRepository(AgentForgeDbContext dbContext) : ILearningRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask AddSignalAsync(
        LearningSignal signal, LearningClassification classification, CancellationToken cancellationToken)
    {
        if (!LearningSignalClassifier.IsConsistent(signal) || classification.SignalId != signal.Id ||
            classification.SignalHash != signal.SignalHash)
            throw new InvalidDataException("Learning signal classification is inconsistent.");
        if (await dbContext.LearningSignals.AnyAsync(item => item.Id == signal.Id.Value, cancellationToken))
            throw new InvalidOperationException("Learning signal already exists.");
        await dbContext.LearningSignals.AddAsync(new LearningSignalEntity
        {
            Id = signal.Id.Value,
            InstallationId = signal.InstallationId.Value,
            Kind = signal.Kind.ToString(),
            Action = classification.Action.ToString(),
            SignalHash = signal.SignalHash,
            ClassificationHash = classification.ClassificationHash,
            CapturedAtUtcTicks = signal.CapturedAt.UtcTicks,
            SignalJson = JsonSerializer.Serialize(signal, JsonOptions),
            ClassificationJson = JsonSerializer.Serialize(classification, JsonOptions),
        }, cancellationToken);
    }

    public async ValueTask<(LearningSignal Signal, LearningClassification Classification)?> FindSignalAsync(
        LearningSignalId id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.LearningSignals.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id.Value, cancellationToken);
        return entity is null ? null : MapSignal(entity);
    }

    public async ValueTask<IReadOnlyList<(LearningSignal Signal, LearningClassification Classification)>> ListSignalsAsync(
        InstallationId installationId,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        if (installationId.Value == Guid.Empty || maximumResults is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        var entities = await dbContext.LearningSignals.AsNoTracking()
            .Where(item => item.InstallationId == installationId.Value)
            .OrderByDescending(item => item.CapturedAtUtcTicks)
            .ThenBy(item => item.Id)
            .Take(maximumResults)
            .ToArrayAsync(cancellationToken);
        return entities.Select(MapSignal).ToArray();
    }

    private static (LearningSignal Signal, LearningClassification Classification) MapSignal(
        LearningSignalEntity entity)
    {
        var signal = JsonSerializer.Deserialize<LearningSignal>(entity.SignalJson, JsonOptions)
            ?? throw new InvalidDataException("Persisted learning signal is empty.");
        var classification = JsonSerializer.Deserialize<LearningClassification>(entity.ClassificationJson, JsonOptions)
            ?? throw new InvalidDataException("Persisted learning classification is empty.");
        if (!LearningSignalClassifier.IsConsistent(signal) || signal.Id.Value != entity.Id ||
            signal.InstallationId.Value != entity.InstallationId || signal.Kind.ToString() != entity.Kind ||
            signal.SignalHash != entity.SignalHash || signal.CapturedAt.UtcTicks != entity.CapturedAtUtcTicks ||
            classification.SignalId != signal.Id || classification.Action.ToString() != entity.Action ||
            classification.SignalHash != signal.SignalHash || classification.ClassificationHash != entity.ClassificationHash)
            throw new InvalidDataException("Persisted learning evidence failed integrity validation.");
        return (signal, classification);
    }

    public async ValueTask AppendCandidateAsync(
        LearningCandidate candidate, long? expectedVersion, CancellationToken cancellationToken)
    {
        if (!LearningCandidateStateMachine.IsConsistent(candidate))
            throw new InvalidDataException("Learning candidate is inconsistent.");
        var actual = await dbContext.LearningCandidateSnapshots
            .Where(item => item.Id == candidate.Id.Value)
            .MaxAsync(item => (long?)item.Version, cancellationToken);
        if (actual != expectedVersion || candidate.Version != (expectedVersion ?? -1) + 1)
            throw new InvalidOperationException("Learning candidate version is stale.");
        await dbContext.LearningCandidateSnapshots.AddAsync(new LearningCandidateSnapshotEntity
        {
            Id = candidate.Id.Value,
            Version = candidate.Version,
            InstallationId = candidate.InstallationId.Value,
            SignalId = candidate.SignalId.Value,
            SkillProposalId = candidate.SkillProposalId.Value,
            SkillId = candidate.SkillId.Value,
            State = candidate.State.ToString(),
            CandidatePackageHash = candidate.CandidatePackageHash,
            BaselinePackageHash = candidate.BaselinePackageHash,
            PreviousSnapshotHash = candidate.PreviousSnapshotHash,
            SnapshotHash = candidate.SnapshotHash,
            UpdatedAtUtcTicks = candidate.UpdatedAt.UtcTicks,
            SnapshotJson = JsonSerializer.Serialize(candidate, JsonOptions),
        }, cancellationToken);
    }

    public async ValueTask<LearningCandidate?> FindLatestCandidateAsync(
        LearningCandidateId id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.LearningCandidateSnapshots.AsNoTracking()
            .Where(item => item.Id == id.Value).OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : MapCandidate(entity);
    }

    public async ValueTask<IReadOnlyList<LearningCandidate>> ListCandidatesAsync(
        InstallationId installationId,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        if (installationId.Value == Guid.Empty || maximumResults is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        var entities = await dbContext.LearningCandidateSnapshots.AsNoTracking()
            .Where(item => item.InstallationId == installationId.Value &&
                item.Version == dbContext.LearningCandidateSnapshots
                    .Where(other => other.Id == item.Id)
                    .Max(other => other.Version))
            .OrderByDescending(item => item.UpdatedAtUtcTicks)
            .ThenBy(item => item.Id)
            .Take(maximumResults)
            .ToArrayAsync(cancellationToken);
        return entities.Select(MapCandidate).ToArray();
    }

    private static LearningCandidate MapCandidate(LearningCandidateSnapshotEntity entity)
    {
        var candidate = JsonSerializer.Deserialize<LearningCandidate>(entity.SnapshotJson, JsonOptions)
            ?? throw new InvalidDataException("Persisted learning candidate is empty.");
        return LearningCandidateStateMachine.IsConsistent(candidate) && candidate.Id.Value == entity.Id &&
            candidate.Version == entity.Version && candidate.InstallationId.Value == entity.InstallationId &&
            candidate.SignalId.Value == entity.SignalId && candidate.SkillProposalId.Value == entity.SkillProposalId &&
            candidate.SkillId.Value == entity.SkillId && candidate.State.ToString() == entity.State &&
            candidate.CandidatePackageHash == entity.CandidatePackageHash &&
            candidate.BaselinePackageHash == entity.BaselinePackageHash &&
            candidate.PreviousSnapshotHash == entity.PreviousSnapshotHash &&
            candidate.SnapshotHash == entity.SnapshotHash && candidate.UpdatedAt.UtcTicks == entity.UpdatedAtUtcTicks
            ? candidate
            : throw new InvalidDataException("Persisted learning candidate failed integrity validation.");
    }

    public async ValueTask AddBundleAsync(SkillBundleDefinition bundle, CancellationToken cancellationToken)
    {
        if (!SkillBundleSynthesizer.IsConsistent(bundle))
            throw new InvalidDataException("Skill bundle is inconsistent.");
        if (await dbContext.SkillBundles.AnyAsync(item =>
                item.Id == bundle.Id.Value && item.Version == bundle.Version.Value, cancellationToken))
            throw new InvalidOperationException("Skill bundle version already exists.");
        await dbContext.SkillBundles.AddAsync(new SkillBundleEntity
        {
            Id = bundle.Id.Value,
            Version = bundle.Version.Value,
            DefinitionHash = bundle.DefinitionHash,
            SourceSignalHash = bundle.SourceSignalHash,
            DefinitionJson = JsonSerializer.Serialize(bundle, JsonOptions),
        }, cancellationToken);
    }

    public async ValueTask AppendBundleProposalAsync(
        SkillBundleProposal proposal, long? expectedVersion, CancellationToken cancellationToken)
    {
        if (!SkillBundleProposalStateMachine.IsConsistent(proposal))
            throw new InvalidDataException("Skill bundle proposal is inconsistent.");
        var actual = await dbContext.SkillBundleProposalSnapshots
            .Where(item => item.Id == proposal.Id.Value)
            .MaxAsync(item => (long?)item.Version, cancellationToken);
        if (actual != expectedVersion || proposal.Version != (expectedVersion ?? -1) + 1)
            throw new InvalidOperationException("Skill bundle proposal version is stale.");
        await dbContext.SkillBundleProposalSnapshots.AddAsync(new SkillBundleProposalSnapshotEntity
        {
            Id = proposal.Id.Value,
            Version = proposal.Version,
            InstallationId = proposal.InstallationId.Value,
            BundleId = proposal.Definition.Id.Value,
            BundleVersion = proposal.Definition.Version.Value,
            State = proposal.State.ToString(),
            DefinitionHash = proposal.Definition.DefinitionHash,
            PreviousSnapshotHash = proposal.PreviousSnapshotHash,
            SnapshotHash = proposal.SnapshotHash,
            UpdatedAtUtcTicks = proposal.UpdatedAt.UtcTicks,
            SnapshotJson = JsonSerializer.Serialize(proposal, JsonOptions),
        }, cancellationToken);
    }

    public async ValueTask<SkillBundleProposal?> FindLatestBundleProposalAsync(
        SkillBundleProposalId id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.SkillBundleProposalSnapshots.AsNoTracking()
            .Where(item => item.Id == id.Value).OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync(cancellationToken);
        if (entity is null) return null;
        var proposal = JsonSerializer.Deserialize<SkillBundleProposal>(entity.SnapshotJson, JsonOptions)
            ?? throw new InvalidDataException("Persisted skill bundle proposal is empty.");
        return SkillBundleProposalStateMachine.IsConsistent(proposal) && proposal.Id.Value == entity.Id &&
            proposal.Version == entity.Version && proposal.InstallationId.Value == entity.InstallationId &&
            proposal.Definition.Id.Value == entity.BundleId && proposal.Definition.Version.Value == entity.BundleVersion &&
            proposal.State.ToString() == entity.State && proposal.Definition.DefinitionHash == entity.DefinitionHash &&
            proposal.PreviousSnapshotHash == entity.PreviousSnapshotHash && proposal.SnapshotHash == entity.SnapshotHash &&
            proposal.UpdatedAt.UtcTicks == entity.UpdatedAtUtcTicks
            ? proposal : throw new InvalidDataException("Persisted skill bundle proposal failed integrity validation.");
    }

    public async ValueTask<SkillBundleDefinition?> FindBundleAsync(
        SkillBundleId id, SkillVersion version, CancellationToken cancellationToken)
    {
        var entity = await dbContext.SkillBundles.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == id.Value && item.Version == version.Value, cancellationToken);
        if (entity is null) return null;
        var bundle = JsonSerializer.Deserialize<SkillBundleDefinition>(entity.DefinitionJson, JsonOptions)
            ?? throw new InvalidDataException("Persisted skill bundle is empty.");
        return SkillBundleSynthesizer.IsConsistent(bundle) && bundle.Id.Value == entity.Id &&
            bundle.Version.Value == entity.Version && bundle.DefinitionHash == entity.DefinitionHash &&
            bundle.SourceSignalHash == entity.SourceSignalHash
            ? bundle : throw new InvalidDataException("Persisted skill bundle failed integrity validation.");
    }
}
