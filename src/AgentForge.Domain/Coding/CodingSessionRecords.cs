using System.Text;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Artifacts;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Coding;

public enum CodingPlanStepKind
{
    Discover,
    Navigate,
    Patch,
    Build,
    Test,
    Analyze,
    Format,
    Coverage,
    Security,
    Dependency,
    Review,
    Publish,
}

public enum CodingPlanStepState
{
    Pending,
    Completed,
    Failed,
}

public sealed record CodingPlanStep(
    string Id,
    CodingPlanStepKind Kind,
    string Target,
    CodingPlanStepState State,
    string? EvidenceHash);

public sealed record CodingPlan(IReadOnlyList<CodingPlanStep> Steps, string PlanHash);

public sealed record CodingReviewReport(
    IReadOnlyList<string> ChangedPaths,
    string DiffHash,
    bool Passed,
    IReadOnlyList<string> FindingCodes,
    DateTimeOffset ReviewedAt,
    string ReportHash);

public enum CodingSessionState
{
    Prepared,
    PatchProposed,
    Patched,
    Verifying,
    Verified,
    Reviewed,
    Completed,
    Failed,
    Cancelled,
}

public sealed record CodingSessionSnapshot(
    CodingSessionId Id,
    InstallationId InstallationId,
    AgentIdentityId AgentId,
    CodingWorkspace Workspace,
    CodingAuthoritySnapshot Authority,
    string RepositoryProfileHash,
    string ObjectiveHash,
    string BackendId,
    string BackendVersion,
    IReadOnlyList<string> InstructionHashes,
    CodingPlan Plan,
    CodingVerificationPlan VerificationPlan,
    CodingSessionState State,
    string? PatchHash,
    ArtifactReference? PatchArtifact,
    CodingPatchReceipt? PatchReceipt,
    IReadOnlyList<CodingVerificationResult> VerificationResults,
    CodingVerificationReceipt? VerificationReceipt,
    CodingReviewReport? ReviewReport,
    DomainFailure? Failure,
    long Version,
    string PreviousSnapshotHash,
    string SnapshotHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ActorId ActorId,
    string IdempotencyKey,
    CorrelationId CorrelationId,
    CorrelationId? CausationId);

public static class CodingSessionStateMachine
{
    public const string EmptyHash =
        "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public static DomainResult<CodingPlan> CreatePlan(IReadOnlyList<(string Id, CodingPlanStepKind Kind, string Target)> steps)
    {
        if (steps is null || steps.Count is < 1 or > 64 || steps.Any(step => !IsBounded(step.Id, 128) ||
                !Enum.IsDefined(step.Kind) || !IsTarget(step.Target)) ||
            steps.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count() != steps.Count)
        {
            return DomainResult.Fail<CodingPlan>(new DomainFailure(
                FailureCode.ValidationFailure, "A coding plan requires bounded unique typed steps and contained targets."));
        }

        var planSteps = steps.Select(step => new CodingPlanStep(
            step.Id, step.Kind, step.Target, CodingPlanStepState.Pending, null)).ToArray();
        return DomainResult.Success(new CodingPlan(planSteps, ComputePlanHash(planSteps)));
    }

    public static DomainResult<CodingSessionSnapshot> Create(
        CodingSessionId id,
        CodingWorkspace workspace,
        CodingAuthoritySnapshot authority,
        string repositoryProfileHash,
        string objectiveHash,
        string backendId,
        string backendVersion,
        IReadOnlyList<string> instructionHashes,
        CodingPlan plan,
        CodingVerificationPlan verificationPlan,
        ActorId actorId,
        string idempotencyKey,
        CorrelationId correlationId,
        CorrelationId? causationId,
        DateTimeOffset createdAt)
    {
        if (id.Value == Guid.Empty || !CodingRecordValidator.IsValid(workspace) || workspace.SessionId != id ||
            !CodingRecordValidator.IsValid(authority) || authority.InstallationId.Value == Guid.Empty ||
            authority.AgentId.Value == Guid.Empty || authority.WorkspaceHash != CodingRecordValidator.ComputeWorkspaceHash(workspace) ||
            !CodingRecordValidator.IsSha256(repositoryProfileHash) || !CodingRecordValidator.IsSha256(objectiveHash) ||
            !IsBounded(backendId, 256) || !Domain.Skills.SkillVersion.TryParse(backendVersion, out _) ||
            instructionHashes is null || instructionHashes.Count > 64 ||
            instructionHashes.Any(hash => !CodingRecordValidator.IsSha256(hash)) ||
            instructionHashes.Distinct(StringComparer.Ordinal).Count() != instructionHashes.Count ||
            !IsValid(plan) || !IsValid(verificationPlan) || !IsBounded(actorId.Value, 256) ||
            !IsBounded(idempotencyKey, 256) || !IsBounded(correlationId.Value, 128) ||
            (causationId is { } causation && !IsBounded(causation.Value, 128)))
        {
            return Invalid("The initial coding session evidence is invalid.");
        }

        var snapshot = new CodingSessionSnapshot(
            id, authority.InstallationId, authority.AgentId, workspace, authority, repositoryProfileHash,
            objectiveHash, backendId, backendVersion, instructionHashes.Order(StringComparer.Ordinal).ToArray(),
            plan, verificationPlan, CodingSessionState.Prepared,
            null, null, null, [], null, null, null, 0, EmptyHash, EmptyHash, createdAt, createdAt,
            actorId, idempotencyKey, correlationId, causationId);
        return DomainResult.Success(snapshot with { SnapshotHash = ComputeHash(snapshot) });
    }

    public static DomainResult<CodingSessionSnapshot> RecordProposal(
        CodingSessionSnapshot current,
        string patchHash,
        ArtifactReference artifact,
        DateTimeOffset occurredAt) =>
        !CanTransition(current, CodingSessionState.Prepared, occurredAt) ||
        !CodingRecordValidator.IsSha256(patchHash) || !IsArtifact(artifact)
            ? Invalid("A patch proposal requires exact prepared-session artifact evidence.")
            : Next(current, CodingSessionState.PatchProposed, occurredAt, patchHash: patchHash, patchArtifact: artifact);

    public static DomainResult<CodingSessionSnapshot> RecordPatch(
        CodingSessionSnapshot current,
        CodingPatchReceipt receipt,
        DateTimeOffset occurredAt) =>
        !CanTransition(current, CodingSessionState.PatchProposed, occurredAt) ||
        receipt is null || receipt.PatchHash != current.PatchHash ||
        CodingPatchValidator.CreatePatchReceipt(receipt.PatchHash, receipt.Files, receipt.AppliedAt) is not
        { IsSuccess: true } recreated || recreated.Value.ReceiptHash != receipt.ReceiptHash
            ? Invalid("Patch application requires exact proposal and receipt evidence.")
            : Next(current, CodingSessionState.Patched, occurredAt, patchReceipt: receipt,
                completedKinds: [CodingPlanStepKind.Patch]);

    public static DomainResult<CodingSessionSnapshot> StartVerification(
        CodingSessionSnapshot current,
        DateTimeOffset occurredAt) => !CanTransition(current, CodingSessionState.Patched, occurredAt)
            ? Invalid("Only a patched session can begin verification.")
            : Next(current, CodingSessionState.Verifying, occurredAt);

    public static DomainResult<CodingSessionSnapshot> RecordVerificationResult(
        CodingSessionSnapshot current,
        CodingVerificationResult result,
        DateTimeOffset occurredAt)
    {
        if (!CanTransition(current, CodingSessionState.Verifying, occurredAt) || result is null ||
            current.VerificationResults.Count >= current.VerificationPlan.Commands.Count ||
            result.Kind != current.VerificationPlan.Commands[current.VerificationResults.Count].Kind ||
            !CodingRecordValidator.IsSha256(result.StandardOutputHash) ||
            !CodingRecordValidator.IsSha256(result.StandardErrorHash) || result.CompletedAt < result.StartedAt)
        {
            return Invalid("A verification result must match the next exact durable command.");
        }

        var results = current.VerificationResults.Append(result with { }).ToArray();
        var command = current.VerificationPlan.Commands[results.Length - 1];
        var terminalFailure = !result.Passed && command.Required;
        var complete = results.Length == current.VerificationPlan.Commands.Count;
        if (!terminalFailure && !complete)
        {
            var intermediate = current with
            {
                VerificationResults = results,
                Version = current.Version + 1,
                PreviousSnapshotHash = current.SnapshotHash,
                SnapshotHash = EmptyHash,
                UpdatedAt = occurredAt,
            };
            return DomainResult.Success(intermediate with { SnapshotHash = ComputeHash(intermediate) });
        }

        var receipt = CodingPatchValidator.CreateVerificationReceipt(current.VerificationPlan, results);
        if (!receipt.IsSuccess)
        {
            return Invalid("The accumulated verification receipt is inconsistent.");
        }

        var completedKinds = results.Where(item => item.Passed).Select(ToPlanKind).Distinct().ToArray();
        return receipt.Value.Passed
            ? Next(current with { VerificationResults = results }, CodingSessionState.Verified, occurredAt,
                verificationReceipt: receipt.Value, completedKinds: completedKinds)
            : Next(current with { VerificationResults = results }, CodingSessionState.Failed, occurredAt,
                verificationReceipt: receipt.Value,
                failure: new DomainFailure(FailureCode.ValidationFailure, "Coding verification failed."),
                completedKinds: completedKinds,
                failedKinds: results.Where(item => !item.Passed).Select(ToPlanKind).Distinct().ToArray());
    }

    public static DomainResult<CodingSessionSnapshot> RecordReview(
        CodingSessionSnapshot current,
        CodingReviewReport report,
        DateTimeOffset occurredAt) =>
        !CanTransition(current, CodingSessionState.Verified, occurredAt) || !IsValid(report)
            ? Invalid("Review requires a verified session and canonical report evidence.")
            : report.Passed
                ? Next(current, CodingSessionState.Reviewed, occurredAt, reviewReport: report,
                    completedKinds: [CodingPlanStepKind.Review])
                : Next(current, CodingSessionState.Failed, occurredAt, reviewReport: report,
                    failure: new DomainFailure(FailureCode.ValidationFailure, "Coding review failed."),
                    failedKinds: [CodingPlanStepKind.Review]);

    public static DomainResult<CodingSessionSnapshot> Complete(
        CodingSessionSnapshot current,
        DateTimeOffset occurredAt) => !CanTransition(current, CodingSessionState.Reviewed, occurredAt) ||
        current.Plan.Steps.Any(step => step.State is not CodingPlanStepState.Completed)
            ? Invalid("Completion requires every coding plan step to have verified evidence.")
            : Next(current, CodingSessionState.Completed, occurredAt);

    public static DomainResult<CodingSessionSnapshot> Cancel(
        CodingSessionSnapshot current,
        DateTimeOffset occurredAt) => !IsConsistent(current) || IsTerminal(current.State) || occurredAt < current.UpdatedAt
            ? Invalid("Only an active current coding session can be cancelled.")
            : Next(current, CodingSessionState.Cancelled, occurredAt,
                failure: new DomainFailure(FailureCode.Cancelled, "Coding session cancelled."));

    public static bool IsConsistent(CodingSessionSnapshot? snapshot) => snapshot is not null &&
        snapshot.Id.Value != Guid.Empty && snapshot.InstallationId == snapshot.Authority.InstallationId &&
        snapshot.AgentId == snapshot.Authority.AgentId && CodingRecordValidator.IsValid(snapshot.Workspace) &&
        CodingRecordValidator.IsValid(snapshot.Authority) && snapshot.Workspace.SessionId == snapshot.Id &&
        snapshot.Authority.WorkspaceHash == CodingRecordValidator.ComputeWorkspaceHash(snapshot.Workspace) &&
        CodingRecordValidator.IsSha256(snapshot.RepositoryProfileHash) && CodingRecordValidator.IsSha256(snapshot.ObjectiveHash) &&
        IsBounded(snapshot.BackendId, 256) && Domain.Skills.SkillVersion.TryParse(snapshot.BackendVersion, out _) &&
        snapshot.InstructionHashes.Count <= 64 && snapshot.InstructionHashes.All(CodingRecordValidator.IsSha256) &&
        snapshot.InstructionHashes.SequenceEqual(snapshot.InstructionHashes.Order(StringComparer.Ordinal), StringComparer.Ordinal) &&
        IsValid(snapshot.Plan) && IsValid(snapshot.VerificationPlan) && Enum.IsDefined(snapshot.State) &&
        (snapshot.PatchHash is null || CodingRecordValidator.IsSha256(snapshot.PatchHash)) &&
        (snapshot.PatchArtifact is null || IsArtifact(snapshot.PatchArtifact)) &&
        (snapshot.PatchReceipt is null || snapshot.PatchReceipt.PatchHash == snapshot.PatchHash &&
            CodingPatchValidator.CreatePatchReceipt(
                snapshot.PatchReceipt.PatchHash, snapshot.PatchReceipt.Files, snapshot.PatchReceipt.AppliedAt) is
            { IsSuccess: true } patchReceipt && patchReceipt.Value.ReceiptHash == snapshot.PatchReceipt.ReceiptHash) &&
        snapshot.VerificationResults.Count <= snapshot.VerificationPlan.Commands.Count &&
        snapshot.VerificationResults.Where((result, index) => result.Kind != snapshot.VerificationPlan.Commands[index].Kind).Any() == false &&
        (snapshot.VerificationReceipt is null || snapshot.VerificationReceipt.PlanHash == snapshot.VerificationPlan.PlanHash &&
            CodingPatchValidator.CreateVerificationReceipt(snapshot.VerificationPlan, snapshot.VerificationReceipt.Results) is
            { IsSuccess: true } verificationReceipt &&
            verificationReceipt.Value.ReceiptHash == snapshot.VerificationReceipt.ReceiptHash) &&
        (snapshot.ReviewReport is null || IsValid(snapshot.ReviewReport)) && snapshot.Version >= 0 &&
        (snapshot.Failure is null || Enum.IsDefined(snapshot.Failure.Code) && IsBounded(snapshot.Failure.Message, 2_048)) &&
        CodingRecordValidator.IsSha256(snapshot.PreviousSnapshotHash) && CodingRecordValidator.IsSha256(snapshot.SnapshotHash) &&
        snapshot.UpdatedAt >= snapshot.CreatedAt && IsBounded(snapshot.ActorId.Value, 256) &&
        IsBounded(snapshot.IdempotencyKey, 256) && IsBounded(snapshot.CorrelationId.Value, 128) &&
        (snapshot.CausationId is null || IsBounded(snapshot.CausationId.Value.Value, 128)) &&
        HasStateShape(snapshot) &&
        snapshot.SnapshotHash == ComputeHash(snapshot);

    public static CodingReviewReport CreateReviewReport(
        IReadOnlyList<string> changedPaths,
        string diffHash,
        bool passed,
        IReadOnlyList<string> findingCodes,
        DateTimeOffset reviewedAt)
    {
        var paths = changedPaths.Order(StringComparer.Ordinal).ToArray();
        var findings = findingCodes.Order(StringComparer.Ordinal).ToArray();
        var builder = new StringBuilder();
        foreach (var path in paths) CodingPatchValidator.Append(builder, path);
        CodingPatchValidator.Append(builder, diffHash); CodingPatchValidator.Append(builder, passed);
        foreach (var finding in findings) CodingPatchValidator.Append(builder, finding);
        CodingPatchValidator.Append(builder, reviewedAt.UtcTicks);
        return new CodingReviewReport(paths, diffHash, passed, findings, reviewedAt,
            CodingPatchValidator.Hash(builder.ToString()));
    }

    private static DomainResult<CodingSessionSnapshot> Next(
        CodingSessionSnapshot current,
        CodingSessionState state,
        DateTimeOffset occurredAt,
        string? patchHash = null,
        ArtifactReference? patchArtifact = null,
        CodingPatchReceipt? patchReceipt = null,
        CodingVerificationReceipt? verificationReceipt = null,
        CodingReviewReport? reviewReport = null,
        DomainFailure? failure = null,
        IReadOnlyList<CodingPlanStepKind>? completedKinds = null,
        IReadOnlyList<CodingPlanStepKind>? failedKinds = null)
    {
        var steps = current.Plan.Steps.Select(step =>
            completedKinds?.Contains(step.Kind) == true ? step with
            {
                State = CodingPlanStepState.Completed,
                EvidenceHash = EvidenceFor(step.Kind, patchReceipt, verificationReceipt, reviewReport),
            } : failedKinds?.Contains(step.Kind) == true ? step with
            {
                State = CodingPlanStepState.Failed,
                EvidenceHash = EvidenceFor(step.Kind, patchReceipt, verificationReceipt, reviewReport),
            } : step).ToArray();
        var next = current with
        {
            State = state,
            PatchHash = patchHash ?? current.PatchHash,
            PatchArtifact = patchArtifact ?? current.PatchArtifact,
            PatchReceipt = patchReceipt ?? current.PatchReceipt,
            VerificationReceipt = verificationReceipt ?? current.VerificationReceipt,
            ReviewReport = reviewReport ?? current.ReviewReport,
            Failure = failure,
            Plan = new CodingPlan(steps, ComputePlanHash(steps)),
            Version = current.Version + 1,
            PreviousSnapshotHash = current.SnapshotHash,
            SnapshotHash = EmptyHash,
            UpdatedAt = occurredAt,
        };
        return DomainResult.Success(next with { SnapshotHash = ComputeHash(next) });
    }

    private static string? EvidenceFor(
        CodingPlanStepKind kind,
        CodingPatchReceipt? patch,
        CodingVerificationReceipt? verification,
        CodingReviewReport? review) => kind switch
        {
            CodingPlanStepKind.Patch => patch?.ReceiptHash,
            CodingPlanStepKind.Review => review?.ReportHash,
            _ => verification?.ReceiptHash,
        };

    private static CodingPlanStepKind ToPlanKind(CodingVerificationResult result) => result.Kind switch
    {
        CodingVerificationKind.Build => CodingPlanStepKind.Build,
        CodingVerificationKind.Test => CodingPlanStepKind.Test,
        CodingVerificationKind.Analyzer => CodingPlanStepKind.Analyze,
        CodingVerificationKind.Format => CodingPlanStepKind.Format,
        CodingVerificationKind.Coverage => CodingPlanStepKind.Coverage,
        CodingVerificationKind.Security => CodingPlanStepKind.Security,
        CodingVerificationKind.Dependency => CodingPlanStepKind.Dependency,
        CodingVerificationKind.Review => CodingPlanStepKind.Review,
        CodingVerificationKind.Publish => CodingPlanStepKind.Publish,
        _ => CodingPlanStepKind.Review,
    };

    private static bool IsValid(CodingPlan plan) => plan is not null && plan.Steps.Count is >= 1 and <= 64 &&
        plan.Steps.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count() == plan.Steps.Count &&
        plan.Steps.All(step => IsBounded(step.Id, 128) && Enum.IsDefined(step.Kind) && IsTarget(step.Target) &&
            Enum.IsDefined(step.State) && (step.EvidenceHash is null || CodingRecordValidator.IsSha256(step.EvidenceHash))) &&
        plan.PlanHash == ComputePlanHash(plan.Steps);

    private static bool IsValid(CodingVerificationPlan plan) =>
        CodingPatchValidator.CreateVerificationPlan(plan.Commands) is { IsSuccess: true } recreated &&
        recreated.Value.PlanHash == plan.PlanHash;

    private static bool IsValid(CodingReviewReport report) => report is not null &&
        report.ChangedPaths.Count is >= 1 and <= 128 && report.ChangedPaths.All(CodingPatchValidator.IsPath) &&
        report.ChangedPaths.SequenceEqual(report.ChangedPaths.Order(StringComparer.Ordinal), StringComparer.Ordinal) &&
        report.ChangedPaths.Distinct(StringComparer.Ordinal).Count() == report.ChangedPaths.Count &&
        CodingRecordValidator.IsSha256(report.DiffHash) && report.FindingCodes.Count <= 128 &&
        report.FindingCodes.All(value => IsBounded(value, 256)) &&
        report.FindingCodes.SequenceEqual(report.FindingCodes.Order(StringComparer.Ordinal), StringComparer.Ordinal) &&
        report.ReportHash == CreateReviewReport(
            report.ChangedPaths, report.DiffHash, report.Passed, report.FindingCodes, report.ReviewedAt).ReportHash;

    private static bool IsArtifact(ArtifactReference artifact) => CodingRecordValidator.IsSha256(artifact.ContentHash) &&
        artifact.Length is > 0 and <= 4_194_304 && IsBounded(artifact.MediaType, 256);

    private static string ComputePlanHash(IReadOnlyList<CodingPlanStep> steps)
    {
        var builder = new StringBuilder();
        foreach (var step in steps)
        {
            CodingPatchValidator.Append(builder, step.Id); CodingPatchValidator.Append(builder, step.Kind);
            CodingPatchValidator.Append(builder, step.Target); CodingPatchValidator.Append(builder, step.State);
            CodingPatchValidator.Append(builder, step.EvidenceHash ?? string.Empty);
        }
        return CodingPatchValidator.Hash(builder.ToString());
    }

    private static string ComputeHash(CodingSessionSnapshot snapshot)
    {
        var builder = new StringBuilder();
        CodingPatchValidator.Append(builder, snapshot.Id); CodingPatchValidator.Append(builder, snapshot.InstallationId);
        CodingPatchValidator.Append(builder, snapshot.AgentId); CodingPatchValidator.Append(builder, snapshot.Authority.WorkspaceHash);
        CodingPatchValidator.Append(builder, snapshot.RepositoryProfileHash); CodingPatchValidator.Append(builder, snapshot.ObjectiveHash);
        CodingPatchValidator.Append(builder, snapshot.BackendId); CodingPatchValidator.Append(builder, snapshot.BackendVersion);
        foreach (var instructionHash in snapshot.InstructionHashes) CodingPatchValidator.Append(builder, instructionHash);
        CodingPatchValidator.Append(builder, snapshot.Plan.PlanHash); CodingPatchValidator.Append(builder, snapshot.VerificationPlan.PlanHash);
        CodingPatchValidator.Append(builder, snapshot.State); CodingPatchValidator.Append(builder, snapshot.PatchHash ?? string.Empty);
        CodingPatchValidator.Append(builder, snapshot.PatchArtifact?.ContentHash ?? string.Empty);
        CodingPatchValidator.Append(builder, snapshot.PatchReceipt?.ReceiptHash ?? string.Empty);
        foreach (var result in snapshot.VerificationResults)
        {
            CodingPatchValidator.Append(builder, result.Kind); CodingPatchValidator.Append(builder, result.Passed);
            CodingPatchValidator.Append(builder, result.ExitCode); CodingPatchValidator.Append(builder, result.StandardOutputHash);
            CodingPatchValidator.Append(builder, result.StandardErrorHash); CodingPatchValidator.Append(builder, result.StartedAt.UtcTicks);
            CodingPatchValidator.Append(builder, result.CompletedAt.UtcTicks); CodingPatchValidator.Append(builder, result.SandboxEvidence);
        }
        CodingPatchValidator.Append(builder, snapshot.VerificationReceipt?.ReceiptHash ?? string.Empty);
        CodingPatchValidator.Append(builder, snapshot.ReviewReport?.ReportHash ?? string.Empty);
        CodingPatchValidator.Append(builder, snapshot.Failure?.Code ?? default); CodingPatchValidator.Append(builder, snapshot.Failure?.Message ?? string.Empty);
        CodingPatchValidator.Append(builder, snapshot.Version); CodingPatchValidator.Append(builder, snapshot.PreviousSnapshotHash);
        CodingPatchValidator.Append(builder, snapshot.CreatedAt.UtcTicks); CodingPatchValidator.Append(builder, snapshot.UpdatedAt.UtcTicks);
        CodingPatchValidator.Append(builder, snapshot.ActorId); CodingPatchValidator.Append(builder, snapshot.IdempotencyKey);
        CodingPatchValidator.Append(builder, snapshot.CorrelationId); CodingPatchValidator.Append(builder, snapshot.CausationId?.Value ?? string.Empty);
        return CodingPatchValidator.Hash(builder.ToString());
    }

    private static bool CanTransition(CodingSessionSnapshot current, CodingSessionState required, DateTimeOffset at) =>
        IsConsistent(current) && current.State == required && at >= current.UpdatedAt;

    private static bool IsTerminal(CodingSessionState state) => state is
        CodingSessionState.Completed or CodingSessionState.Failed or CodingSessionState.Cancelled;

    private static bool HasStateShape(CodingSessionSnapshot snapshot) => snapshot.State switch
    {
        CodingSessionState.Prepared => snapshot.PatchHash is null && snapshot.PatchArtifact is null &&
            snapshot.PatchReceipt is null && snapshot.VerificationResults.Count == 0 &&
            snapshot.VerificationReceipt is null && snapshot.ReviewReport is null && snapshot.Failure is null,
        CodingSessionState.PatchProposed => snapshot.PatchHash is not null && snapshot.PatchArtifact is not null &&
            snapshot.PatchReceipt is null && snapshot.VerificationResults.Count == 0 &&
            snapshot.VerificationReceipt is null && snapshot.ReviewReport is null && snapshot.Failure is null,
        CodingSessionState.Patched => snapshot.PatchReceipt is not null && snapshot.VerificationResults.Count == 0 &&
            snapshot.VerificationReceipt is null && snapshot.ReviewReport is null && snapshot.Failure is null,
        CodingSessionState.Verifying => snapshot.PatchReceipt is not null &&
            snapshot.VerificationResults.Count < snapshot.VerificationPlan.Commands.Count &&
            snapshot.VerificationReceipt is null && snapshot.ReviewReport is null && snapshot.Failure is null,
        CodingSessionState.Verified => snapshot.VerificationResults.Count == snapshot.VerificationPlan.Commands.Count &&
            snapshot.VerificationReceipt is { Passed: true } && snapshot.ReviewReport is null && snapshot.Failure is null,
        CodingSessionState.Reviewed => snapshot.VerificationReceipt is { Passed: true } &&
            snapshot.ReviewReport is { Passed: true } && snapshot.Failure is null,
        CodingSessionState.Completed => snapshot.VerificationReceipt is { Passed: true } &&
            snapshot.ReviewReport is { Passed: true } && snapshot.Failure is null,
        CodingSessionState.Failed => snapshot.Failure is not null &&
            (snapshot.VerificationReceipt is { Passed: false } || snapshot.ReviewReport is { Passed: false }),
        CodingSessionState.Cancelled => snapshot.Failure?.Code is FailureCode.Cancelled,
        _ => false,
    };

    private static bool IsTarget(string value) => value == "." || CodingPatchValidator.IsPath(value);

    private static bool IsBounded(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && value.All(character => !char.IsControl(character));

    private static DomainResult<CodingSessionSnapshot> Invalid(string message) =>
        DomainResult.Fail<CodingSessionSnapshot>(new DomainFailure(FailureCode.ValidationFailure, message));
}
