using AgentForge.Domain.Artifacts;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Skills;

namespace AgentForge.UnitTests;

public sealed class SkillGovernanceStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 4, 0, 0, TimeSpan.Zero);
    private const string HashA = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string HashC = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void Promotion_requires_evaluation_separate_approval_exact_baseline_and_passing_canary()
    {
        var baseline = Version("1.0.0", HashA, SkillPackageStatus.Active, ["repo:read"]);
        var candidate = Version("1.1.0", HashB, SkillPackageStatus.Installed, ["repo:read", "repo:write"]);
        var current = Create(candidate, baseline);

        Assert.Equal(["repo:write"], current.AddedPermissions);
        current = SkillGovernanceStateMachine.Evaluate(
            current,
            Evaluation(passed: true),
            Now.AddSeconds(1)).Value;
        Assert.Equal(SkillProposalState.AwaitingApproval, current.State);

        var selfApproval = SkillGovernanceStateMachine.Approve(
            current, new ActorId("proposer"), HashA, Now.AddSeconds(2));
        Assert.False(selfApproval.IsSuccess);
        Assert.Equal(FailureCode.PolicyDenied, selfApproval.Failure!.Code);

        var stale = SkillGovernanceStateMachine.Approve(
            current, new ActorId("governor"), HashC, Now.AddSeconds(2));
        Assert.False(stale.IsSuccess);

        current = SkillGovernanceStateMachine.Approve(
            current, new ActorId("governor"), HashA, Now.AddSeconds(2)).Value;
        current = SkillGovernanceStateMachine.StartCanary(current, Now.AddSeconds(3)).Value;
        current = SkillGovernanceStateMachine.FinishCanary(
            current,
            new SkillCanaryReceipt(true, 0.8m, 0.9m, HashC),
            HashA,
            Now.AddSeconds(4)).Value;

        Assert.Equal(SkillProposalState.Promoted, current.State);
        Assert.Equal(new ActorId("governor"), current.ApprovedBy);
        Assert.True(SkillGovernanceStateMachine.IsConsistent(current));
    }

    [Fact]
    public void Failed_deterministic_evidence_vetoes_before_approval()
    {
        var current = Create(
            Version("2.0.0", HashB, SkillPackageStatus.Installed),
            Version("1.0.0", HashA, SkillPackageStatus.Active));
        current = SkillGovernanceStateMachine.Evaluate(
            current,
            Evaluation(passed: false),
            Now.AddSeconds(1)).Value;

        Assert.Equal(SkillProposalState.Rejected, current.State);
        Assert.False(SkillGovernanceStateMachine.Approve(
            current,
            new ActorId("governor"),
            HashA,
            Now.AddSeconds(2)).IsSuccess);
    }

    [Fact]
    public void Canary_regression_quarantines_and_stale_promotion_race_conflicts()
    {
        var current = Create(
            Version("2.0.0", HashB, SkillPackageStatus.Installed),
            Version("1.0.0", HashA, SkillPackageStatus.Active));
        current = SkillGovernanceStateMachine.Evaluate(current, Evaluation(true), Now.AddSeconds(1)).Value;
        current = SkillGovernanceStateMachine.Approve(
            current, new ActorId("governor"), HashA, Now.AddSeconds(2)).Value;
        current = SkillGovernanceStateMachine.StartCanary(current, Now.AddSeconds(3)).Value;

        var stale = SkillGovernanceStateMachine.FinishCanary(
            current,
            new SkillCanaryReceipt(true, 0.8m, 0.9m, HashC),
            HashC,
            Now.AddSeconds(4));
        Assert.False(stale.IsSuccess);
        Assert.Equal(FailureCode.ConcurrencyConflict, stale.Failure!.Code);

        current = SkillGovernanceStateMachine.FinishCanary(
            current,
            new SkillCanaryReceipt(false, 0.8m, 0.7m, HashC),
            HashA,
            Now.AddSeconds(4)).Value;
        Assert.Equal(SkillProposalState.Quarantined, current.State);
    }

    [Fact]
    public void Promoted_proposal_can_roll_back_only_with_hash_bound_evidence()
    {
        var current = Promote();
        var rollback = SkillGovernanceStateMachine.Rollback(current, HashC, Now.AddMinutes(1));

        Assert.True(rollback.IsSuccess);
        Assert.Equal(SkillProposalState.RolledBack, rollback.Value.State);
        Assert.False(rollback.Value.Canary!.Passed);
        Assert.True(SkillGovernanceStateMachine.IsConsistent(rollback.Value));
        Assert.False(SkillGovernanceStateMachine.Rollback(
            rollback.Value,
            HashC,
            Now.AddMinutes(2)).IsSuccess);
    }

    [Fact]
    public void Active_run_snapshot_pins_exact_versions_artifacts_permissions_and_hash()
    {
        var root = Version("1.0.0", HashA, SkillPackageStatus.Active, ["repo:read"]);
        var dependency = Version(
            "2.0.0",
            HashB,
            SkillPackageStatus.Installed,
            ["tool:read"],
            "skill:dependency");
        var result = SkillGovernanceStateMachine.CreateRunSnapshot(
            new SkillRunSnapshotId(Guid.Parse("60000000-0000-0000-0000-000000000001")),
            root.InstallationId,
            [root, dependency],
            new ActorId("worker"),
            "run-snapshot-001",
            new CorrelationId("run-snapshot"),
            null,
            Now);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(["skill:dependency", "skill:fixture"],
            result.Value.Selections.Select(item => item.SkillId.Value));
        Assert.True(SkillGovernanceStateMachine.IsConsistent(result.Value));
        Assert.False(SkillGovernanceStateMachine.IsConsistent(result.Value with
        {
            Selections = result.Value.Selections.Select(item => item with { PackageHash = HashC }).ToArray(),
        }));
    }

    [Fact]
    public void Run_snapshot_rejects_quarantined_archived_duplicate_and_cross_installation_versions()
    {
        var active = Version("1.0.0", HashA, SkillPackageStatus.Active);
        var quarantined = Version("2.0.0", HashB, SkillPackageStatus.Quarantined);
        Assert.False(CreateRun([active, quarantined]).IsSuccess);
        Assert.False(CreateRun([active, active]).IsSuccess);
        Assert.False(CreateRun([
            active,
            Version("2.0.0", HashB, SkillPackageStatus.Installed, installationId: Guid.NewGuid()),
        ]).IsSuccess);
    }

    private static SkillProposal Promote()
    {
        var current = Create(
            Version("2.0.0", HashB, SkillPackageStatus.Installed),
            Version("1.0.0", HashA, SkillPackageStatus.Active));
        current = SkillGovernanceStateMachine.Evaluate(current, Evaluation(true), Now.AddSeconds(1)).Value;
        current = SkillGovernanceStateMachine.Approve(
            current, new ActorId("governor"), HashA, Now.AddSeconds(2)).Value;
        current = SkillGovernanceStateMachine.StartCanary(current, Now.AddSeconds(3)).Value;
        return SkillGovernanceStateMachine.FinishCanary(
            current,
            new SkillCanaryReceipt(true, 0.8m, 0.9m, HashC),
            HashA,
            Now.AddSeconds(4)).Value;
    }

    private static SkillProposal Create(
        RegisteredSkillVersion candidate,
        RegisteredSkillVersion? baseline) => SkillGovernanceStateMachine.Create(
        new SkillProposalId(Guid.Parse("60000000-0000-0000-0000-000000000002")),
        candidate,
        baseline,
        new ActorId("proposer"),
        new CorrelationId("proposal"),
        null,
        Now).Value;

    private static SkillEvaluationReceipt Evaluation(bool passed) => new(
        passed,
        passed,
        passed,
        0.8m,
        passed ? 0.9m : 0.7m,
        HashC);

    private static DomainResult<SkillRunSnapshot> CreateRun(IReadOnlyList<RegisteredSkillVersion> versions) =>
        SkillGovernanceStateMachine.CreateRunSnapshot(
            new SkillRunSnapshotId(Guid.NewGuid()),
            versions[0].InstallationId,
            versions,
            new ActorId("worker"),
            "run-snapshot",
            new CorrelationId("run-snapshot"),
            null,
            Now);

    private static RegisteredSkillVersion Version(
        string version,
        string hash,
        SkillPackageStatus status,
        IReadOnlyList<string>? permissions = null,
        string id = "skill:fixture",
        Guid? installationId = null) => new(
        new InstallationId(installationId ?? Guid.Parse("60000000-0000-0000-0000-000000000003")),
        new SkillPackageDescriptor(
            new SkillId(id),
            ParseVersion(version),
            "Fixture",
            [],
            new SkillRequirements(["linux"], ["text"], ["tool:repo.read"]),
            permissions ?? ["repo:read"],
            HashC,
            hash,
            null),
        new ArtifactReference(hash, 100, "application/vnd.agentforge.skill", Now),
        status,
        SkillPackageProvenance.User,
        0,
        Now,
        Now,
        new ActorId("operator"),
        new CorrelationId("skill-register"));

    private static SkillVersion ParseVersion(string value)
    {
        Assert.True(SkillVersion.TryParse(value, out var version));
        return version;
    }
}
