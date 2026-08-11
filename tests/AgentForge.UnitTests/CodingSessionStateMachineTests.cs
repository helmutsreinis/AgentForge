using AgentForge.Domain.Agents;
using AgentForge.Domain.Artifacts;
using AgentForge.Domain.Coding;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Tools;

namespace AgentForge.UnitTests;

public sealed class CodingSessionStateMachineTests
{
    private const string GitHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Hash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Session_requires_ordered_durable_command_evidence_before_completion()
    {
        var current = CreateInitial();
        var proposal = CodingSessionStateMachine.RecordProposal(
            current, Hash, new ArtifactReference(Hash, 100, "application/json", Now), Now.AddSeconds(1));
        Assert.True(proposal.IsSuccess);
        var patchReceipt = CodingPatchValidator.CreatePatchReceipt(Hash,
        [
            new CodingFileChangeEvidence("src/value.cs", Hash, Hash, 1, 1),
        ], Now.AddSeconds(2));
        Assert.True(patchReceipt.IsSuccess);
        var patched = CodingSessionStateMachine.RecordPatch(proposal.Value, patchReceipt.Value, Now.AddSeconds(2));
        Assert.True(patched.IsSuccess);
        var verifying = CodingSessionStateMachine.StartVerification(patched.Value, Now.AddSeconds(3));
        Assert.True(verifying.IsSuccess);

        var build = Result(CodingVerificationKind.Build, Now.AddSeconds(4));
        var first = CodingSessionStateMachine.RecordVerificationResult(verifying.Value, build, Now.AddSeconds(4));
        Assert.True(first.IsSuccess);
        Assert.Equal(CodingSessionState.Verifying, first.Value.State);
        Assert.Single(first.Value.VerificationResults);

        var outOfOrder = CodingSessionStateMachine.RecordVerificationResult(
            first.Value, build, Now.AddSeconds(5));
        Assert.False(outOfOrder.IsSuccess);
        var test = Result(CodingVerificationKind.Test, Now.AddSeconds(5));
        var verified = CodingSessionStateMachine.RecordVerificationResult(first.Value, test, Now.AddSeconds(5));
        Assert.True(verified.IsSuccess);
        Assert.Equal(CodingSessionState.Verified, verified.Value.State);

        var report = CodingSessionStateMachine.CreateReviewReport(
            ["src/value.cs"], Hash, true, [], Now.AddSeconds(6));
        var reviewed = CodingSessionStateMachine.RecordReview(verified.Value, report, Now.AddSeconds(6));
        Assert.True(reviewed.IsSuccess);
        var completed = CodingSessionStateMachine.Complete(reviewed.Value, Now.AddSeconds(7));
        Assert.True(completed.IsSuccess);
        Assert.Equal(CodingSessionState.Completed, completed.Value.State);
        Assert.Equal(7, completed.Value.Version);
        Assert.All(completed.Value.Plan.Steps, step => Assert.Equal(CodingPlanStepState.Completed, step.State));
        Assert.True(CodingSessionStateMachine.IsConsistent(completed.Value));
        Assert.False(CodingSessionStateMachine.IsConsistent(completed.Value with { SnapshotHash = Hash }));
    }

    [Fact]
    public void Active_session_can_cancel_but_terminal_session_cannot_transition_again()
    {
        var current = CreateInitial();
        var cancelled = CodingSessionStateMachine.Cancel(current, Now.AddSeconds(1));
        Assert.True(cancelled.IsSuccess);
        Assert.Equal(CodingSessionState.Cancelled, cancelled.Value.State);
        Assert.Equal(FailureCode.Cancelled, cancelled.Value.Failure?.Code);
        Assert.True(CodingSessionStateMachine.IsConsistent(cancelled.Value));
        Assert.False(CodingSessionStateMachine.Cancel(cancelled.Value, Now.AddSeconds(2)).IsSuccess);
        Assert.False(CodingSessionStateMachine.RecordProposal(
            cancelled.Value, Hash, new ArtifactReference(Hash, 100, "application/json", Now),
            Now.AddSeconds(2)).IsSuccess);
    }

    private static CodingSessionSnapshot CreateInitial()
    {
        var id = new CodingSessionId(Guid.Parse("309cd507-bc39-447f-9fc2-009c5ec3479a"));
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "agentforge-state-fixture"));
        var workspace = new CodingWorkspace(id, root, root, GitHash, GitHash, "codex/state", true, Now);
        var authority = new CodingAuthoritySnapshot(
            new InstallationId(Guid.Parse("a02ce1c1-13b1-4455-9a1d-7caf52a2f913")),
            new AgentIdentityId(Guid.Parse("0145c310-ca77-46b7-aa70-7b1aad875d14")),
            1, Hash, Hash, Hash, Hash, CodingRecordValidator.ComputeWorkspaceHash(workspace),
            new ActorId("operator"), new CorrelationId("coding-state"), null);
        var plan = CodingSessionStateMachine.CreatePlan(
        [
            ("patch", CodingPlanStepKind.Patch, "src/value.cs"),
            ("build", CodingPlanStepKind.Build, "."),
            ("test", CodingPlanStepKind.Test, "."),
            ("review", CodingPlanStepKind.Review, "."),
        ]);
        var executable = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var verification = CodingPatchValidator.CreateVerificationPlan(
        [
            Command(CodingVerificationKind.Build, executable),
            Command(CodingVerificationKind.Test, executable),
        ]);
        Assert.True(plan.IsSuccess);
        Assert.True(verification.IsSuccess);
        var created = CodingSessionStateMachine.Create(
            id, workspace, authority, Hash, Hash, "backend:fixture", "1.0.0", [], plan.Value,
            verification.Value, authority.ActorId, "coding-state", authority.CorrelationId, null, Now);
        Assert.True(created.IsSuccess);
        return created.Value;
    }

    private static CodingVerificationCommand Command(CodingVerificationKind kind, string executable) => new(
        kind, executable, [kind.ToString().ToLowerInvariant()], ".", new Dictionary<string, string>(),
        TimeSpan.FromMinutes(1), 16_384, ProcessSandboxKind.Container, ProcessNetworkPolicy.Denied, true);

    private static CodingVerificationResult Result(CodingVerificationKind kind, DateTimeOffset at) => new(
        kind, true, 0, Hash, Hash, at, at.AddSeconds(1), "container-fixture");
}
