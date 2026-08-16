using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Setup;
using AgentForge.Abstractions.Skills;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Setup;
using AgentForge.Domain.Skills;

namespace AgentForge.Host.Http;

internal sealed record CreateSkillProposalWebRequest(string SkillId, string Version);

internal sealed record TransitionSkillProposalWebRequest(
    string Action,
    long ExpectedVersion,
    bool? TargetPassed,
    bool? HoldoutPassed,
    bool? AdversarialPassed,
    bool? Passed,
    decimal? BaselineMetric,
    decimal? CandidateMetric,
    string? EvidenceHash);

internal sealed record PreviewAgentSkillGrantWebRequest(
    long ExpectedInstallationVersion,
    long ExpectedAgentVersion,
    string SkillId,
    bool Grant);

internal static partial class ReadyAdminEndpoints
{
    private static async Task<IResult> CreateSkillProposalAsync(
        CreateSkillProposalWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        ISkillProposalRepository proposals,
        ISkillGovernanceService governance,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        var skillId = (request.SkillId ?? string.Empty).Trim();
        var versionText = (request.Version ?? string.Empty).Trim();
        if (!SkillIdText(skillId) || !SkillVersion.TryParse(versionText, out var version))
        {
            return Problem(context, 400, "Invalid skill proposal",
                "Select an exact installed skill ID and semantic version.", "validation-failure");
        }

        var session = acquired.Session!;
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"skill-proposal:{skillId}:{versionText}:{key}";
        var requestHash = SnapshotHash(new { skillId, version = version!.Value });
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another proposal.", "idempotency-conflict");
            }

            var openProposal = (await proposals.ListLatestAsync(
                    session.InstallationId, 1_000, cancellationToken))
                .FirstOrDefault(item => item.SkillId.Value == skillId &&
                    item.CandidateVersion == version &&
                    item.State is SkillProposalState.Proposed or SkillProposalState.AwaitingApproval or
                        SkillProposalState.Approved or SkillProposalState.Canary);
            if (openProposal is not null)
            {
                return Problem(context, 409, "Activation already in progress",
                    $"Proposal {openProposal.Id} already governs this exact candidate version.",
                    "concurrency-conflict");
            }

            var stable = StableRequestIdentity(
                session.InstallationId, "skill-proposal", $"{skillId}:{version.Value}");
            var proposalId = new SkillProposalId(new Guid(stable.AsSpan(0, 16)));
            var correlation = new CorrelationId($"skill-proposal:{Convert.ToHexStringLower(stable.AsSpan(16, 16))}");
            var result = await governance.CreateProposalAsync(
                proposalId,
                session.InstallationId,
                new SkillId(skillId),
                version,
                SkillGovernanceActor(session.InstallationId, "proposer"),
                correlation,
                null,
                cancellationToken);
            if (!result.IsSuccess)
            {
                return DomainProblem(context, result.Failure!, "Skill proposal failed");
            }

            var response = SkillProposalResponse(result.Value);
            StoreIdempotentResult(session, scopedKey, requestHash, response);
            return Results.Created($"/api/v1/admin/skills/proposals/{proposalId.Value:D}", response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static async Task<IResult> TransitionSkillProposalAsync(
        Guid proposalId,
        TransitionSkillProposalWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        ISkillProposalRepository proposals,
        ISkillGovernanceService governance,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
        var needsEvidence = action is "evaluate" or "finish-canary" or "rollback";
        if (proposalId == Guid.Empty || request.ExpectedVersion < 0 ||
            action is not ("evaluate" or "approve" or "start-canary" or "finish-canary" or "rollback") ||
            needsEvidence && !SkillPackageValidator.IsHash(request.EvidenceHash))
        {
            return Problem(context, 400, "Invalid skill transition",
                "Provide a supported transition, current version, and hash-addressed evidence where required.",
                "validation-failure");
        }

        var session = acquired.Session!;
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"skill-transition:{proposalId:D}:{action}:{key}";
        var requestHash = SnapshotHash(new
        {
            proposalId,
            action,
            request.ExpectedVersion,
            request.TargetPassed,
            request.HoldoutPassed,
            request.AdversarialPassed,
            request.Passed,
            request.BaselineMetric,
            request.CandidateMetric,
            request.EvidenceHash,
        });
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another transition.", "idempotency-conflict");
            }

            var id = new SkillProposalId(proposalId);
            var current = await proposals.FindLatestAsync(id, cancellationToken);
            if (current is null || current.InstallationId != session.InstallationId)
            {
                return Problem(context, 404, "Skill proposal not found",
                    "The selected proposal does not belong to this installation.", "not-found");
            }

            var result = action switch
            {
                "evaluate" when request.TargetPassed is not null && request.HoldoutPassed is not null &&
                    request.AdversarialPassed is not null && request.BaselineMetric is not null &&
                    request.CandidateMetric is not null => await governance.EvaluateAsync(
                        id, request.ExpectedVersion,
                        new SkillEvaluationReceipt(
                            request.TargetPassed.Value,
                            request.HoldoutPassed.Value,
                            request.AdversarialPassed.Value,
                            request.BaselineMetric.Value,
                            request.CandidateMetric.Value,
                            request.EvidenceHash!), cancellationToken),
                "approve" => await governance.ApproveAsync(
                    id, request.ExpectedVersion,
                    SkillGovernanceActor(session.InstallationId, "governor"), cancellationToken),
                "start-canary" => await governance.StartCanaryAsync(
                    id, request.ExpectedVersion, cancellationToken),
                "finish-canary" when request.Passed is not null && request.BaselineMetric is not null &&
                    request.CandidateMetric is not null => await governance.FinishCanaryAsync(
                        id, request.ExpectedVersion,
                        new SkillCanaryReceipt(
                            request.Passed.Value,
                            request.BaselineMetric.Value,
                            request.CandidateMetric.Value,
                            request.EvidenceHash!), cancellationToken),
                "rollback" => await governance.RollbackAsync(
                    id, request.ExpectedVersion, request.EvidenceHash!, cancellationToken),
                _ => DomainResult.Fail<SkillProposal>(new DomainFailure(
                    FailureCode.ValidationFailure,
                    "The transition is missing its required evaluation fields.")),
            };
            if (!result.IsSuccess)
            {
                return DomainProblem(context, result.Failure!, "Skill transition failed");
            }

            var response = SkillProposalResponse(result.Value);
            StoreIdempotentResult(session, scopedKey, requestHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static async Task<IResult> PreviewAgentSkillGrantAsync(
        Guid agentId,
        PreviewAgentSkillGrantWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IAgentIdentityRepository agents,
        ISkillRegistryRepository skills,
        ILocalAdministratorRepository administrators,
        ISecretStore secretStore,
        ISetupProfileEditor editor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }
        var skillId = (request.SkillId ?? string.Empty).Trim();
        if (agentId == Guid.Empty || !SkillIdText(skillId))
        {
            return Problem(context, 400, "Invalid skill grant",
                "Select an exact agent and skill before previewing the grant.", "validation-failure");
        }

        var session = acquired.Session!;
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"skill-grant-preview:{agentId:D}:{skillId}:{request.Grant}:{key}";
        var requestHash = SnapshotHash(new
        {
            agentId,
            request.ExpectedInstallationVersion,
            request.ExpectedAgentVersion,
            skillId,
            request.Grant,
        });
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another grant preview.", "idempotency-conflict");
            }

            var agent = await agents.FindByIdAsync(new AgentIdentityId(agentId), cancellationToken);
            if (agent is null || agent.InstallationId != session.InstallationId)
            {
                return Problem(context, 404, "Agent not found",
                    "The selected agent does not belong to this installation.", "not-found");
            }
            var currentlyGranted = agent.CapabilityPolicy.SkillGrants.Contains(skillId, StringComparer.Ordinal);
            if (currentlyGranted == request.Grant)
            {
                return Problem(context, 400, "No grant change",
                    request.Grant ? "The agent already has this exact skill grant." : "The agent does not have this skill grant.",
                    "validation-failure");
            }

            var active = await skills.FindActiveAsync(session.InstallationId, new SkillId(skillId), cancellationToken);
            if (request.Grant && active is null)
            {
                return Problem(context, 403, "Active skill required",
                    "Complete evaluation, independent approval, and canary promotion before granting the skill.",
                    "policy-denied");
            }

            var grants = request.Grant
                ? agent.CapabilityPolicy.SkillGrants.Append(skillId).Order(StringComparer.Ordinal).ToArray()
                : agent.CapabilityPolicy.SkillGrants.Where(item => !string.Equals(item, skillId, StringComparison.Ordinal)).ToArray();
            var candidate = AgentCandidate(agent) with
            {
                CapabilityPolicy = agent.CapabilityPolicy with { SkillGrants = grants },
            };
            var administratorCredential = await MaterializeAdministratorCredentialAsync(
                session.InstallationId, administrators, secretStore, cancellationToken);
            if (!administratorCredential.IsSuccess)
            {
                return DomainProblem(context, administratorCredential.Failure!, "Skill grant preview failed");
            }

            var stable = StableRequestIdentity(session.InstallationId, "skill-grant-preview", key);
            var correlation = new CorrelationId($"skill-grant:{Convert.ToHexStringLower(stable.AsSpan(0, 16))}");
            DomainResult<AgentEditPreview> preview;
            await using (var credential = administratorCredential.Value)
            {
                preview = await editor.PreviewAgentAsync(new PreviewAgentEditRequest(
                    agent.Id,
                    request.ExpectedInstallationVersion,
                    request.ExpectedAgentVersion,
                    candidate,
                    session.ActorId,
                    correlation,
                    credential.Value), cancellationToken);
            }
            if (!preview.IsSuccess)
            {
                return DomainProblem(context, preview.Failure!, "Skill grant preview failed");
            }
            if (preview.Value.Changes.Count != 1 || preview.Value.Changes[0].Path != "agent.capabilityPolicy")
            {
                return Problem(context, 403, "Exact grant required",
                    "The preview changed authority outside the single requested skill grant.", "policy-denied");
            }

            RetainBoundedPreviews(session.SkillGrantPreviews, 7);
            session.SkillGrantPreviews[preview.Value.RequestHash] = new ReadyAgentSkillGrantPreview(
                agent.Id,
                request.ExpectedInstallationVersion,
                request.ExpectedAgentVersion,
                new SkillId(skillId),
                active?.Package.Version,
                active?.Package.PackageHash,
                request.Grant,
                candidate,
                correlation,
                preview.Value.RequestHash);
            var response = new
            {
                previewHash = preview.Value.RequestHash,
                action = request.Grant ? "grant" : "revoke",
                agent = new { id = agent.Id.Value, agent.Name, agent.Version },
                skill = new
                {
                    id = skillId,
                    version = active?.Package.Version.Value,
                    packageHash = active?.Package.PackageHash,
                },
                changes = preview.Value.Changes.Select(ChangeResponse),
                warning = "This authorizes immutable skill instructions only. Tool, network, file, message, and device authority remain separate and denied unless explicitly granted.",
                correlationId = correlation.Value,
            };
            StoreIdempotentResult(session, scopedKey, requestHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static async Task<IResult> ApplyAgentSkillGrantAsync(
        Guid agentId,
        ReadyEditApplyWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        ISkillRegistryRepository skills,
        ILocalAdministratorRepository administrators,
        ISecretStore secretStore,
        ISetupProfileEditor editor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        var session = acquired.Session!;
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"skill-grant-apply:{agentId:D}:{key}";
        var requestHash = SnapshotHash(new { agentId, request.PreviewHash });
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another grant.", "idempotency-conflict");
            }
            if (!Text(request.PreviewHash, 128) ||
                !session.SkillGrantPreviews.TryGetValue(request.PreviewHash, out var approved) ||
                approved.AgentId != new AgentIdentityId(agentId))
            {
                return Problem(context, 403, "Approved preview required",
                    "Preview the exact skill authority change in this operator session before applying it.",
                    "policy-denied");
            }

            if (approved.Grant)
            {
                var active = await skills.FindActiveAsync(session.InstallationId, approved.SkillId, cancellationToken);
                if (active is null || active.Package.Version != approved.ActiveVersion ||
                    !string.Equals(active.Package.PackageHash, approved.PackageHash, StringComparison.Ordinal))
                {
                    return Problem(context, 409, "Active skill changed",
                        "The promoted skill version changed after preview; review the new exact package.",
                        "concurrency-conflict");
                }
            }

            var administratorCredential = await MaterializeAdministratorCredentialAsync(
                session.InstallationId, administrators, secretStore, cancellationToken);
            if (!administratorCredential.IsSuccess)
            {
                return DomainProblem(context, administratorCredential.Failure!, "Skill grant failed");
            }

            DomainResult<AgentEditResult> applied;
            await using (var credential = administratorCredential.Value)
            {
                applied = await editor.ApplyAgentAsync(new ApplyAgentEditRequest(
                    approved.AgentId,
                    approved.ExpectedInstallationVersion,
                    approved.ExpectedAgentVersion,
                    approved.Candidate,
                    approved.RequestHash,
                    session.ActorId,
                    approved.CorrelationId,
                    credential.Value), cancellationToken);
            }
            if (!applied.IsSuccess)
            {
                return DomainProblem(context, applied.Failure!, "Skill grant failed");
            }

            var response = new
            {
                installationVersion = applied.Value.Installation.Version,
                agent = AgentEditResponse(applied.Value.Agent),
                skillId = approved.SkillId.Value,
                granted = approved.Grant,
                changes = applied.Value.Changes.Select(ChangeResponse),
                previewHash = applied.Value.RequestHash,
                correlationId = approved.CorrelationId.Value,
            };
            session.SkillGrantPreviews.TryRemove(request.PreviewHash, out _);
            StoreIdempotentResult(session, scopedKey, requestHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static ActorId SkillGovernanceActor(InstallationId installationId, string role) =>
        new($"skill-{role}:{installationId.Value:N}");

    private static AgentIdentityCandidate AgentCandidate(AgentIdentity agent) => new(
        agent.Name,
        agent.Expertise,
        agent.Mission,
        agent.PreferredLanguage,
        agent.TimeZone,
        agent.ResponseStyle,
        agent.DefaultWorkspace,
        agent.ModelPolicy,
        agent.MemoryPolicy,
        agent.CapabilityPolicy,
        agent.Budget,
        agent.ChildLimits,
        agent.LearningPolicy);

    private static object SkillProposalResponse(SkillProposal proposal) => new
    {
        id = proposal.Id.Value,
        skillId = proposal.SkillId.Value,
        candidateVersion = proposal.CandidateVersion.Value,
        proposal.CandidatePackageHash,
        baselineVersion = proposal.BaselineVersion?.Value,
        proposal.BaselinePackageHash,
        addedPermissions = proposal.AddedPermissions,
        removedPermissions = proposal.RemovedPermissions,
        state = proposal.State.ToString(),
        proposal.Version,
        proposal.SnapshotHash,
        proposedBy = proposal.ProposedBy.Value,
        approvedBy = proposal.ApprovedBy?.Value,
        activeAuthority = proposal.State is SkillProposalState.Promoted,
        nextGate = proposal.State switch
        {
            SkillProposalState.Proposed => "deterministic-evaluation",
            SkillProposalState.AwaitingApproval => "independent-approval",
            SkillProposalState.Approved => "scoped-canary",
            SkillProposalState.Canary => "canary-evaluation",
            SkillProposalState.Promoted => "rollback-available",
            _ => "terminal",
        },
        evaluation = proposal.Evaluation,
        canary = proposal.Canary,
        proposal.CreatedAt,
        proposal.UpdatedAt,
    };
}
