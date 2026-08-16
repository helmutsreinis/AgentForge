using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Learning;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Learning;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Skills;

namespace AgentForge.Host.Http;

internal sealed record ProposeLearningCandidateWebRequest(
    string SkillId,
    string Version,
    string Description,
    IReadOnlyList<string>? RequestedPermissions);

internal sealed record TransitionLearningCandidateWebRequest(
    string Action,
    long ExpectedVersion,
    bool? TargetPassed,
    bool? HoldoutPassed,
    bool? AdversarialPassed,
    bool? PermissionDiffApproved,
    bool? Passed,
    decimal? BaselineMetric,
    decimal? CandidateMetric,
    IReadOnlyList<string>? FindingCodes,
    string? EvidenceHash);

internal static partial class ReadyAdminEndpoints
{
    private static async Task<IResult> ListLearningCandidatesAsync(
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        ILearningRepository learning,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireAsync(
            context, sessions, stateReader, clock, requireCsrf: false, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        var candidates = await learning.ListCandidatesAsync(
            acquired.Session!.InstallationId, 100, cancellationToken);
        return Results.Ok(new
        {
            candidates = candidates.Select(LearningCandidateResponse),
            correlationId = context.TraceIdentifier,
        });
    }

    private static async Task<IResult> ProposeLearningCandidateAsync(
        Guid signalId,
        ProposeLearningCandidateWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        ILearningRepository repository,
        ILearningCandidateProposalService proposals,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(
            context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        var skillId = (request.SkillId ?? string.Empty).Trim();
        var versionText = (request.Version ?? string.Empty).Trim();
        var description = (request.Description ?? string.Empty).Trim();
        var permissions = (request.RequestedPermissions ?? [])
            .Select(value => value?.Trim() ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (signalId == Guid.Empty || !SkillVersion.TryParse(versionText, out var version) ||
            !Text(skillId, 256) || !skillId.StartsWith("skill:", StringComparison.Ordinal) ||
            !Text(description, 512) || permissions.Length > 32 ||
            permissions.Any(value => !Text(value, 256)) ||
            permissions.Distinct(StringComparer.Ordinal).Count() != permissions.Length)
        {
            return Problem(context, 400, "Invalid proposal",
                "Provide a valid skill ID, semantic version, bounded description, and unique declared permissions.",
                "validation-failure");
        }

        var session = acquired.Session!;
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"learning-candidate:{signalId:D}:{idempotencyKey}";
        var requestHash = SnapshotHash(new { signalId, skillId, version = version!.Value, description, permissions });
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existingResult))
            {
                return string.Equals(existingResult.RequestHash, requestHash, StringComparison.Ordinal)
                    ? Replay(context, existingResult.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another learning proposal.",
                        "idempotency-conflict");
            }

            var signal = await repository.FindSignalAsync(new LearningSignalId(signalId), cancellationToken);
            if (signal is null || signal.Value.Signal.InstallationId != session.InstallationId)
            {
                return Problem(context, 404, "Learning signal not found",
                    "The selected learning signal does not belong to this installation.", "not-found");
            }
            if (signal.Value.Classification.Action is not LearningAction.NewSkill)
            {
                return Problem(context, 403, "New-skill evidence required",
                    "Only evidence classified as NewSkill may enter the isolated proposal workflow.",
                    "policydenied");
            }

            var stable = StableRequestIdentity(session.InstallationId, "learning-candidate", idempotencyKey);
            var candidateId = new LearningCandidateId(new Guid(stable.AsSpan(0, 16)));
            var proposalId = new SkillProposalId(new Guid(stable.AsSpan(16, 16)));
            var roles = LearningRoles(session.InstallationId);
            var proposed = await proposals.ProposeNewSkillAsync(new ProposeNewSkillFromSignalRequest(
                candidateId,
                proposalId,
                signal.Value.Signal.Id,
                new SkillId(skillId),
                version!,
                description,
                permissions,
                roles), cancellationToken);
            if (!proposed.IsSuccess)
            {
                return DomainProblem(context, proposed.Failure!, "Learning proposal failed");
            }

            var response = LearningCandidateResponse(proposed.Value.Candidate);
            StoreIdempotentResult(session, scopedKey, requestHash, response);
            if (proposed.Value.WasReplay)
            {
                return Replay(context, response);
            }
            return Results.Created(
                $"/api/v1/admin/learning/candidates/{candidateId.Value:D}", response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static LearningRoleAssignments LearningRoles(InstallationId installationId)
    {
        var scope = installationId.Value.ToString("N");
        return new LearningRoleAssignments(
            new ActorId($"learning-worker:{scope}"),
            new ActorId($"learning-proposer:{scope}"),
            new ActorId($"learning-verifier:{scope}"),
            new ActorId($"learning-critic:{scope}"),
            new ActorId($"learning-governor:{scope}"));
    }

    private static async Task<IResult> TransitionLearningCandidateAsync(
        Guid candidateId,
        TransitionLearningCandidateWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        ILearningRepository repository,
        ILearningGovernanceService governance,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(
            context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
        var findings = (request.FindingCodes ?? [])
            .Select(value => value?.Trim() ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (candidateId == Guid.Empty || request.ExpectedVersion < 0 ||
            action is not ("verify" or "critique" or "approve" or "start-canary" or "finish-canary" or "rollback") ||
            findings.Length > 128 || findings.Any(value => !Text(value, 128)) ||
            findings.Distinct(StringComparer.Ordinal).Count() != findings.Length ||
            RequiresEvidence(action) && !SkillPackageValidator.IsHash(request.EvidenceHash))
        {
            return Problem(context, 400, "Invalid learning transition",
                "Provide a supported transition, current version, and bounded hash-addressed evidence.",
                "validation-failure");
        }

        var session = acquired.Session!;
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"learning-transition:{candidateId:D}:{action}:{idempotencyKey}";
        var requestHash = SnapshotHash(new
        {
            candidateId,
            action,
            request.ExpectedVersion,
            request.TargetPassed,
            request.HoldoutPassed,
            request.AdversarialPassed,
            request.PermissionDiffApproved,
            request.Passed,
            request.BaselineMetric,
            request.CandidateMetric,
            findings,
            request.EvidenceHash,
        });
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existingResult))
            {
                return string.Equals(existingResult.RequestHash, requestHash, StringComparison.Ordinal)
                    ? Replay(context, existingResult.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another learning transition.",
                        "idempotency-conflict");
            }

            var id = new LearningCandidateId(candidateId);
            var current = await repository.FindLatestCandidateAsync(id, cancellationToken);
            if (current is null || current.InstallationId != session.InstallationId)
            {
                return Problem(context, 404, "Learning candidate not found",
                    "The selected learning candidate does not belong to this installation.", "not-found");
            }

            var transitioned = action switch
            {
                "verify" when request.TargetPassed is not null && request.HoldoutPassed is not null &&
                    request.AdversarialPassed is not null && request.PermissionDiffApproved is not null &&
                    request.BaselineMetric is not null && request.CandidateMetric is not null =>
                    await governance.VerifyAsync(id, request.ExpectedVersion, current.Roles.Verifier,
                        new LearningCandidateEvaluation(
                            request.TargetPassed.Value,
                            request.HoldoutPassed.Value,
                            request.AdversarialPassed.Value,
                            request.PermissionDiffApproved.Value,
                            request.BaselineMetric.Value,
                            request.CandidateMetric.Value,
                            request.EvidenceHash!), cancellationToken),
                "critique" when request.Passed is not null =>
                    await governance.CritiqueAsync(id, request.ExpectedVersion, current.Roles.Critic,
                        new LearningCritique(request.Passed.Value, findings, request.EvidenceHash!), cancellationToken),
                "approve" => await governance.ApproveAsync(
                    id, request.ExpectedVersion, current.Roles.Governor, cancellationToken),
                "start-canary" => await governance.StartCanaryAsync(
                    id, request.ExpectedVersion, current.Roles.Governor, cancellationToken),
                "finish-canary" when request.Passed is not null && request.BaselineMetric is not null &&
                    request.CandidateMetric is not null => await governance.FinishCanaryAsync(
                        id, request.ExpectedVersion, current.Roles.Governor, request.Passed.Value,
                        request.BaselineMetric.Value, request.CandidateMetric.Value,
                        request.EvidenceHash!, cancellationToken),
                "rollback" => await governance.RollbackAsync(
                    id, request.ExpectedVersion, current.Roles.Governor, request.EvidenceHash!, cancellationToken),
                _ => DomainResult.Fail<LearningCandidate>(new DomainFailure(
                    FailureCode.ValidationFailure,
                    "The selected transition is missing its required evaluation fields.")),
            };
            if (!transitioned.IsSuccess)
            {
                return DomainProblem(context, transitioned.Failure!, "Learning transition failed");
            }

            var response = LearningCandidateResponse(transitioned.Value);
            StoreIdempotentResult(session, scopedKey, requestHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static bool RequiresEvidence(string action) =>
        action is "verify" or "critique" or "finish-canary" or "rollback";

    private static object LearningCandidateResponse(LearningCandidate candidate) => new
    {
        id = candidate.Id.Value,
        signalId = candidate.SignalId.Value,
        action = candidate.Action.ToString(),
        skillProposalId = candidate.SkillProposalId.Value,
        skillId = candidate.SkillId.Value,
        candidateVersion = candidate.CandidateVersion.Value,
        candidate.CandidatePackageHash,
        requestedPermissions = candidate.RequestedPermissions,
        roles = new
        {
            worker = candidate.Roles.Worker.Value,
            proposer = candidate.Roles.Proposer.Value,
            verifier = candidate.Roles.Verifier.Value,
            critic = candidate.Roles.Critic.Value,
            governor = candidate.Roles.Governor.Value,
        },
        state = candidate.State.ToString(),
        candidate.Version,
        candidate.SnapshotHash,
        proposalWorkspace = new
        {
            candidate.ProposalWorkspace.ContentHash,
            candidate.ProposalWorkspace.Length,
            candidate.ProposalWorkspace.MediaType,
        },
        evaluation = candidate.Evaluation is null ? null : new
        {
            candidate.Evaluation.TargetPassed,
            candidate.Evaluation.HoldoutPassed,
            candidate.Evaluation.AdversarialPassed,
            candidate.Evaluation.PermissionDiffApproved,
            candidate.Evaluation.BaselineScore,
            candidate.Evaluation.CandidateScore,
            candidate.Evaluation.EvidenceHash,
        },
        critique = candidate.Critique is null ? null : new
        {
            candidate.Critique.Passed,
            candidate.Critique.FindingCodes,
            candidate.Critique.EvidenceHash,
        },
        activeAuthority = candidate.State is LearningCandidateState.Promoted,
        nextGate = candidate.State switch
        {
            LearningCandidateState.Proposed => "deterministic-verification",
            LearningCandidateState.Verified => "independent-critique",
            LearningCandidateState.Critiqued => "governor-approval",
            LearningCandidateState.Approved => "scoped-canary",
            LearningCandidateState.Canary => "canary-evaluation",
            _ => "terminal",
        },
        candidate.CreatedAt,
        candidate.UpdatedAt,
    };
}
