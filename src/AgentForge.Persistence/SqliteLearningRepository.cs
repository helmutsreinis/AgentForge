using System.Text.Json;
using AgentForge.Abstractions.Learning;
using AgentForge.Domain.Learning;
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
        if (entity is null) return null;
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
        if (entity is null) return null;
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
