using System.Collections.Immutable;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Memory;
using AgentForge.Abstractions.Search;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Memory;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Search;

namespace AgentForge.Host.Http;

internal sealed record ReadyMemoryCreateWebRequest(
    long ExpectedAgentVersion,
    Guid AgentId,
    string Kind,
    string Content,
    bool IsCorrection,
    int? RetentionDays);

internal sealed record ReadyMemoryDeleteWebRequest(
    long ExpectedAgentVersion,
    Guid AgentId);

internal sealed record ReadyResearchWebRequest(
    long ExpectedAgentVersion,
    Guid AgentId,
    string Query,
    int MaximumResults,
    IReadOnlyList<string>? ProviderIds);

internal sealed record ReadyResearchApplyWebRequest(string PreviewHash);

internal static partial class ReadyAdminEndpoints
{
    private static async Task<IResult> SearchMemoryAsync(
        Guid agentId,
        string? query,
        int? maximumResults,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IAgentIdentityRepository agents,
        IMemoryService memory,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireAsync(
            context, sessions, stateReader, clock, requireCsrf: false, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        var agent = await agents.FindByIdAsync(new AgentIdentityId(agentId), cancellationToken);
        if (agent is null || agent.InstallationId != acquired.Session!.InstallationId)
        {
            return Problem(context, 404, "Agent not found",
                "The selected agent does not belong to this installation.", "not-found");
        }
        var scope = MemoryScope(agent, acquired.Session.ActorId);
        if (scope is null || !Text(query?.Trim(), 256) || maximumResults is < 1 or > 50)
        {
            return Problem(context, 400, "Memory search unavailable",
                "Choose an Agent or Operator memory scope and enter a bounded search query.", "validation-failure");
        }

        var result = await memory.SearchAsync(new MemoryQuery(
            acquired.Session.InstallationId,
            agent.Id,
            scope,
            query!.Trim(),
            Enum.GetValues<MemoryKind>().ToImmutableArray(),
            maximumResults ?? 20,
            clock.UtcNow), cancellationToken);
        return result.IsSuccess
            ? Results.Ok(new
            {
                agent = new { id = agent.Id.Value, agent.Name, agent.Version },
                scope,
                memories = result.Value.Select(MemoryResponse),
                correlationId = context.TraceIdentifier,
            })
            : DomainProblem(context, result.Failure!, "Memory search failed");
    }

    private static async Task<IResult> CreateMemoryAsync(
        ReadyMemoryCreateWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IAgentIdentityRepository agents,
        IMemoryService memory,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        var session = acquired.Session!;
        if (request is null || request.AgentId == Guid.Empty || !PromptText(request.Content, 65_536) ||
            !TryEnum(request.Kind, out MemoryKind kind) || kind is not (MemoryKind.User or MemoryKind.Procedural))
        {
            return Problem(context, 400, "Invalid memory",
                "Ready administration accepts bounded User or Procedural memory only.", "validation-failure");
        }
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"memory-create:{request.AgentId:D}:{idempotencyKey}";
        var requestHash = SnapshotHash(request);
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another memory entry.", "idempotency-conflict");
            }
            var agent = await agents.FindByIdAsync(new AgentIdentityId(request.AgentId), cancellationToken);
            if (agent is null || agent.InstallationId != session.InstallationId)
            {
                return Problem(context, 404, "Agent not found",
                    "The selected agent does not belong to this installation.", "not-found");
            }
            if (agent.Version != request.ExpectedAgentVersion)
            {
                return Problem(context, 409, "Agent changed",
                    "Refresh memory policy before saving this entry.", "concurrency-conflict");
            }
            var scope = MemoryScope(agent, session.ActorId);
            var retentionDays = request.RetentionDays ?? agent.MemoryPolicy.RetentionDays;
            if (scope is null || agent.MemoryPolicy.RetentionDays < 1 || retentionDays < 1 ||
                retentionDays > agent.MemoryPolicy.RetentionDays)
            {
                return Problem(context, 403, "Memory policy denied",
                    "The exact agent version does not permit this scope or retention period.", "policy-denied");
            }

            var stable = StableRequestIdentity(session.InstallationId, $"memory-create:{request.AgentId:D}", idempotencyKey);
            var entryId = new MemoryEntryId(new Guid(stable.AsSpan(0, 16)));
            var correlation = new CorrelationId($"admin-memory:{Convert.ToHexStringLower(stable)}");
            var sourceKind = request.IsCorrection ? MemorySourceKind.UserCorrection : MemorySourceKind.UserInput;
            var evidenceHash = SnapshotHash(new
            {
                Kind = kind.ToString(),
                Content = request.Content,
                SourceKind = sourceKind.ToString(),
                AgentVersion = agent.Version,
                RetentionDays = retentionDays,
            });
            var result = await memory.CreateAsync(new CreateMemoryRequest(
                entryId,
                session.InstallationId,
                agent.Id,
                scope,
                kind,
                request.Content,
                new MemorySource(sourceKind, $"ready-admin:{Convert.ToHexStringLower(stable)}", evidenceHash, null),
                clock.UtcNow.AddDays(retentionDays),
                session.ActorId,
                correlation,
                null,
                StoredIdempotencyKey("memory-create", idempotencyKey)), cancellationToken);
            if (!result.IsSuccess) return DomainProblem(context, result.Failure!, "Memory creation failed");
            var response = new
            {
                memory = MemoryResponse(result.Value),
                correlationId = correlation.Value,
            };
            StoreIdempotentResult(session, scopedKey, requestHash, response);
            return Results.Created($"/api/v1/admin/memory?agentId={agent.Id.Value:D}", response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static async Task<IResult> DeleteMemoryAsync(
        Guid memoryId,
        ReadyMemoryDeleteWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IAgentIdentityRepository agents,
        IMemoryService memory,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        var session = acquired.Session!;
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"memory-delete:{memoryId:D}:{idempotencyKey}";
        var requestHash = SnapshotHash(new { memoryId, request.ExpectedAgentVersion, request.AgentId });
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another memory deletion.", "idempotency-conflict");
            }
            var agent = await agents.FindByIdAsync(new AgentIdentityId(request.AgentId), cancellationToken);
            var scope = agent is null ? null : MemoryScope(agent, session.ActorId);
            if (memoryId == Guid.Empty || agent is null || agent.InstallationId != session.InstallationId ||
                agent.Version != request.ExpectedAgentVersion || scope is null)
            {
                return Problem(context, 409, "Memory authority changed",
                    "Refresh the exact agent memory scope before deleting this entry.", "concurrency-conflict");
            }
            var correlation = new CorrelationId(context.TraceIdentifier);
            var result = await memory.DeleteAsync(new DeleteMemoryRequest(
                new MemoryEntryId(memoryId),
                session.InstallationId,
                agent.Id,
                scope,
                session.ActorId,
                correlation,
                null), cancellationToken);
            if (!result.IsSuccess) return DomainProblem(context, result.Failure!, "Memory deletion failed");
            var response = new { deleted = result.Value, correlationId = correlation.Value };
            StoreIdempotentResult(session, scopedKey, requestHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static async Task<IResult> ListResearchProvidersAsync(
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IEnumerable<ISearchProvider> providers,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireAsync(
            context, sessions, stateReader, clock, requireCsrf: false, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        return Results.Ok(new
        {
            providers = providers.Select(item => item.Descriptor)
                .OrderBy(item => item.Priority).ThenBy(item => item.Id, StringComparer.Ordinal),
            boundary = "Search is an exact operator-approved context acquisition. The model receives immutable citations and never receives network authority.",
            correlationId = context.TraceIdentifier,
        });
    }

    private static async Task<IResult> PreviewResearchAsync(
        ReadyResearchWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IAgentIdentityRepository agents,
        IEnumerable<ISearchProvider> providers,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        var session = acquired.Session!;
        var agent = await agents.FindByIdAsync(new AgentIdentityId(request.AgentId), cancellationToken);
        if (agent is null || agent.InstallationId != session.InstallationId ||
            agent.Version != request.ExpectedAgentVersion)
        {
            return Problem(context, 409, "Agent changed",
                "Refresh the exact agent version before approving research.", "concurrency-conflict");
        }
        var catalog = providers.Select(item => item.Descriptor).ToDictionary(item => item.Id, StringComparer.Ordinal);
        IEnumerable<string> requestedProviders = request.ProviderIds is { Count: > 0 }
            ? request.ProviderIds
            : catalog.Keys;
        var selected = requestedProviders
            .Select(item => item.Trim()).Order(StringComparer.Ordinal).ToArray();
        if (!Text(request.Query?.Trim(), 512) || request.MaximumResults is < 1 or > 20 ||
            selected.Length is < 1 or > 4 || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length ||
            selected.Any(item => !catalog.ContainsKey(item)))
        {
            return Problem(context, 400, "Invalid research request",
                catalog.Count == 0 ? "No search provider is configured." :
                "Choose one to four available providers and a bounded query/result limit.",
                catalog.Count == 0 ? "unsupported-capability" : "validation-failure");
        }
        var requestHash = SnapshotHash(new
        {
            Kind = "ready-research-v1",
            AgentId = agent.Id.Value,
            agent.Version,
            Query = request.Query!.Trim(),
            request.MaximumResults,
            ProviderIds = selected,
        });
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"research-preview:{idempotencyKey}";
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another research preview.", "idempotency-conflict");
            }
            var stable = StableRequestIdentity(session.InstallationId, "research-preview", idempotencyKey);
            var correlation = new CorrelationId($"admin-research:{Convert.ToHexStringLower(stable)}");
            RetainBoundedPreviews(session.ResearchPreviews, 7);
            session.ResearchPreviews[requestHash] = new ReadyResearchPreview(
                agent.Id, agent.Version, request.Query!.Trim(), request.MaximumResults,
                selected, requestHash, correlation);
            var response = new
            {
                previewHash = requestHash,
                agent = new { id = agent.Id.Value, agent.Name, agent.Version },
                query = request.Query!.Trim(),
                request.MaximumResults,
                providers = selected.Select(id => catalog[id]),
                warning = "Results are untrusted reference material. Citations cannot change policy, grant tools, or issue instructions.",
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

    private static async Task<IResult> ApplyResearchAsync(
        ReadyResearchApplyWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IAgentIdentityRepository agents,
        IResearchService research,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        var session = acquired.Session!;
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"research-apply:{idempotencyKey}";
        if (session.Results.TryGetValue(scopedKey, out var replay))
        {
            return string.Equals(replay.RequestHash, request.PreviewHash, StringComparison.Ordinal)
                ? Replay(context, replay.Response)
                : Problem(context, 409, "Idempotency conflict",
                    "The idempotency key is already bound to another research execution.", "idempotency-conflict");
        }
        if (!Text(request.PreviewHash, 128) ||
            !session.ResearchPreviews.TryGetValue(request.PreviewHash, out var approved))
        {
            return Problem(context, 403, "Approved research preview required",
                "Preview the exact agent, query, provider set, and result limit before searching.", "policy-denied");
        }
        var agent = await agents.FindByIdAsync(approved.AgentId, cancellationToken);
        if (agent is null || agent.InstallationId != session.InstallationId ||
            agent.Version != approved.ExpectedAgentVersion)
        {
            return Problem(context, 409, "Research authority changed",
                "The approved agent version changed; create another preview.", "concurrency-conflict");
        }

        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, approved.RequestHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another research execution.", "idempotency-conflict");
            }
            var result = await research.ResearchAsync(new SearchRequest(
                approved.Query,
                approved.MaximumResults,
                approved.ProviderIds.ToImmutableArray(),
                $"agent:{approved.AgentId.Value:D}",
                session.ActorId.Value,
                approved.CorrelationId.Value,
                clock.UtcNow,
                TimeSpan.FromMinutes(15)), cancellationToken);
            if (!result.IsSuccess)
            {
                return DomainProblem(context, result.Failure!, "Research failed");
            }
            var receiptHash = SnapshotHash(new
            {
                Kind = "ready-research-receipt-v1",
                approved.AgentId,
                result.Value.QueryHash,
                result.Value.EvidenceHash,
                Citations = result.Value.Citations.Select(item => item.EvidenceHash),
            });
            RetainBoundedPreviews(session.ResearchReceipts, 15);
            session.ResearchReceipts[receiptHash] = new ReadyResearchReceipt(
                approved.AgentId,
                receiptHash,
                result.Value.QueryHash,
                result.Value.Citations,
                result.Value.CreatedAtUtc,
                result.Value.ExpiresAtUtc);
            session.ResearchPreviews.TryRemove(request.PreviewHash, out _);
            var response = new
            {
                receiptHash,
                result.Value.QueryHash,
                citations = result.Value.Citations,
                failures = result.Value.ProviderFailures,
                result.Value.IsCacheHit,
                result.Value.CreatedAtUtc,
                result.Value.ExpiresAtUtc,
                warning = "Attach this receipt to a run explicitly. Citation text remains untrusted data.",
                correlationId = approved.CorrelationId.Value,
            };
            StoreIdempotentResult(session, scopedKey, approved.RequestHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static string? MemoryScope(AgentIdentity agent, ActorId actorId) => agent.MemoryPolicy.Scope switch
    {
        AgentMemoryScope.Agent => $"agent:{agent.Id.Value:D}",
        AgentMemoryScope.Operator => $"operator:{actorId.Value}",
        _ => null,
    };

    private static object MemoryResponse(MemoryEntry entry) => new
    {
        id = entry.Id.Value,
        agentId = entry.AgentId.Value,
        entry.ScopeId,
        kind = entry.Kind.ToString(),
        entry.Content,
        entry.ContentHash,
        source = new
        {
            kind = entry.Source.Kind.ToString(),
            entry.Source.SourceId,
            entry.Source.EvidenceHash,
            uri = entry.Source.SourceUri?.AbsoluteUri,
        },
        entry.CreatedAtUtc,
        entry.ExpiresAtUtc,
        entry.RedactionCount,
        entry.Version,
    };
}
