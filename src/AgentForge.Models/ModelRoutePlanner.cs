using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;

namespace AgentForge.Models;

public sealed class ModelRoutePlanner(
    IModelRouteAuthoritySnapshotReader authorityReader,
    IModelProviderHealthSource healthSource,
    IModelProviderCatalog catalog,
    IModelContextPreparer contextPreparer,
    IModelRouter router,
    IClock clock) : IModelRoutePlanner
{
    private const int MaximumAttemptedProfiles = 8;
    private const int MaximumCandidateProfiles = 256;
    private static readonly TimeSpan MaximumPlanLifetime = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<DomainResult<ModelRoutePlan>> PlanAsync(
        ModelRoutePlanningRequest request,
        CancellationToken cancellationToken)
    {
        var requestValidation = ValidateRequest(request);
        if (!requestValidation.IsSuccess)
        {
            return DomainResult.Fail<ModelRoutePlan>(requestValidation.Failure!);
        }

        request = request with
        {
            AttemptedProfileIds = Array.AsReadOnly(request.AttemptedProfileIds.ToArray()),
        };
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = contextPreparer.Prepare(request.Request);
        if (!prepared.IsSuccess)
        {
            return DomainResult.Fail<ModelRoutePlan>(prepared.Failure!);
        }

        var initialAuthorityResult = await authorityReader.ReadAsync(
            request.InstallationId,
            request.AgentId,
            cancellationToken);
        if (!initialAuthorityResult.IsSuccess)
        {
            return DomainResult.Fail<ModelRoutePlan>(initialAuthorityResult.Failure!);
        }

        var initialAuthoritySnapshot = SnapshotAuthority(initialAuthorityResult.Value);
        if (!initialAuthoritySnapshot.IsSuccess)
        {
            return DomainResult.Fail<ModelRoutePlan>(initialAuthoritySnapshot.Failure!);
        }

        var initialAuthority = initialAuthoritySnapshot.Value;
        var authorityValidation = ValidateAuthority(request, initialAuthority, prepared.Value.Request);
        if (!authorityValidation.IsSuccess)
        {
            return DomainResult.Fail<ModelRoutePlan>(authorityValidation.Failure!);
        }

        var now = clock.UtcNow;
        var candidates = catalog.List()
            .Where(item => string.Equals(item.Model, prepared.Value.Request.Model, StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length == 0)
        {
            return Failure(FailureCode.UnsupportedCapability, "No exact model route is configured.");
        }

        if (candidates.Length > MaximumCandidateProfiles)
        {
            return Failure(FailureCode.ValidationFailure, "The exact model route set exceeds its configured bound.");
        }

        var profilesResult = ValidateProfiles(initialAuthority, candidates, now);
        if (!profilesResult.IsSuccess)
        {
            return DomainResult.Fail<ModelRoutePlan>(profilesResult.Failure!);
        }

        var candidateIds = candidates.Select(item => item.ProfileId).ToHashSet();
        if (request.AttemptedProfileIds.Any(item => !candidateIds.Contains(item)))
        {
            return Failure(
                FailureCode.ConcurrencyConflict,
                "Model attempt history no longer matches the current exact-model catalog.");
        }

        var initialHealthResult = await ReadHealthAsync(cancellationToken);
        if (!initialHealthResult.IsSuccess)
        {
            return DomainResult.Fail<ModelRoutePlan>(initialHealthResult.Failure!);
        }

        var initialHealth = initialHealthResult.Value;
        var initialExclusions = BuildExclusions(candidates, initialHealth, request.AttemptedProfileIds, now);
        var routingRequest = new ModelRoutingRequest(
            prepared.Value.Request,
            initialAuthority.Agent.ModelPolicy,
            request.EstimatedInputTokens,
            initialExclusions);
        var initialSelection = router.SelectRoute(routingRequest);
        if (!initialSelection.IsSuccess)
        {
            return DomainResult.Fail<ModelRoutePlan>(initialSelection.Failure!);
        }

        var selectedDescriptor = candidates.Single(item => item.ProfileId == initialSelection.Value.ProfileId);
        var selectedProfile = profilesResult.Value[selectedDescriptor.ProfileId];
        if (!PersistedProfileAllows(selectedProfile, initialSelection.Value.RequiredCapabilities))
        {
            return Failure(
                FailureCode.UnsupportedCapability,
                "The current durable provider profile does not authorize every required capability.");
        }

        var normalizedRequest = ModelContractValidator.NormalizeRequest(
            prepared.Value.Request,
            selectedDescriptor,
            now);
        if (!normalizedRequest.IsSuccess)
        {
            return DomainResult.Fail<ModelRoutePlan>(normalizedRequest.Failure!);
        }

        var initialAuthorityHash = ComputeAuthorityHash(initialAuthority, candidateIds);
        var initialHealthHash = ComputeHealthHash(candidates, initialHealth);

        var finalAuthorityResult = await authorityReader.ReadAsync(
            request.InstallationId,
            request.AgentId,
            cancellationToken);
        if (!finalAuthorityResult.IsSuccess)
        {
            return DomainResult.Fail<ModelRoutePlan>(finalAuthorityResult.Failure!);
        }

        var finalAuthoritySnapshot = SnapshotAuthority(finalAuthorityResult.Value);
        if (!finalAuthoritySnapshot.IsSuccess)
        {
            return DomainResult.Fail<ModelRoutePlan>(finalAuthoritySnapshot.Failure!);
        }

        var finalAuthority = finalAuthoritySnapshot.Value;
        if (!string.Equals(
            initialAuthorityHash,
            ComputeAuthorityHash(finalAuthority, candidateIds),
            StringComparison.Ordinal))
        {
            return Failure(
                FailureCode.ConcurrencyConflict,
                "Model route authority changed while the plan was being prepared.");
        }

        var finalAuthorityValidation = ValidateAuthority(request, finalAuthority, prepared.Value.Request);
        if (!finalAuthorityValidation.IsSuccess)
        {
            return DomainResult.Fail<ModelRoutePlan>(finalAuthorityValidation.Failure!);
        }

        var finalHealthResult = await ReadHealthAsync(cancellationToken);
        if (!finalHealthResult.IsSuccess)
        {
            return DomainResult.Fail<ModelRoutePlan>(finalHealthResult.Failure!);
        }

        var finalHealth = finalHealthResult.Value;
        var finalHealthHash = ComputeHealthHash(candidates, finalHealth);
        if (!string.Equals(initialHealthHash, finalHealthHash, StringComparison.Ordinal))
        {
            return Failure(
                FailureCode.RecoverableExternalFailure,
                "Model provider health changed while the plan was being prepared.",
                retryable: true);
        }

        var plannedAt = clock.UtcNow;
        var finalExclusions = BuildExclusions(candidates, finalHealth, request.AttemptedProfileIds, plannedAt);
        var finalSelection = router.SelectRoute(routingRequest with { ExcludedProfileIds = finalExclusions });
        if (!finalSelection.IsSuccess ||
            !string.Equals(
                initialSelection.Value.SelectionEvidenceHash,
                finalSelection.IsSuccess ? finalSelection.Value.SelectionEvidenceHash : null,
                StringComparison.Ordinal))
        {
            return Failure(
                FailureCode.RecoverableExternalFailure,
                "Model route eligibility changed while the plan was being prepared.",
                retryable: true);
        }

        var validUntil = ComputeValidUntil(plannedAt, candidates, finalHealth);
        if (validUntil <= plannedAt)
        {
            return Failure(
                FailureCode.RecoverableExternalFailure,
                "Model route evidence expired while the plan was being prepared.",
                retryable: true);
        }

        var route = finalSelection.Value;
        var routeProfile = profilesResult.Value[route.ProfileId];
        var planHash = ComputePlanHash(
            request,
            route,
            routeProfile.Version,
            normalizedRequest.Value.InputHash,
            prepared.Value,
            finalHealthHash,
            plannedAt,
            validUntil);
        return DomainResult.Success(new ModelRoutePlan(
            request.Request.Id,
            request.InstallationId,
            finalAuthority.Installation.Version,
            request.AgentId,
            finalAuthority.Agent.Version,
            routeProfile.Version,
            route,
            normalizedRequest.Value.InputHash,
            prepared.Value.RedactionCount,
            prepared.Value.Policy,
            finalHealthHash,
            plannedAt,
            validUntil,
            planHash));
    }

    private async Task<DomainResult<ModelProviderHealthCatalog>> ReadHealthAsync(
        CancellationToken cancellationToken)
    {
        var evidence = await healthSource.ReadAsync(cancellationToken);
        return evidence.IsSuccess
            ? ModelProviderHealthCatalog.Create(evidence.Value)
            : DomainResult.Fail<ModelProviderHealthCatalog>(evidence.Failure!);
    }

    private static DomainResult<bool> ValidateRequest(ModelRoutePlanningRequest request)
    {
        if (request is null || request.InstallationId.Value == Guid.Empty ||
            request.ExpectedInstallationVersion < 1 || request.AgentId.Value == Guid.Empty ||
            request.ExpectedAgentVersion < 1 || request.Request is null ||
            request.EstimatedInputTokens is < 0 or > 10_000_000 ||
            request.AttemptedProfileIds is null ||
            request.AttemptedProfileIds.Count > MaximumAttemptedProfiles ||
            request.AttemptedProfileIds.Any(item => item.Value == Guid.Empty) ||
            request.AttemptedProfileIds.Distinct().Count() != request.AttemptedProfileIds.Count)
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Model route planning identity, versions, estimates, or attempt history are invalid."));
        }

        return DomainResult.Success(true);
    }

    private static DomainResult<bool> ValidateAuthority(
        ModelRoutePlanningRequest request,
        ModelRouteAuthoritySnapshot authority,
        ModelRequest preparedRequest)
    {
        if (authority is null || authority.Installation is null || authority.Agent is null ||
            authority.ProviderProfiles is null)
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Model route authority snapshot is incomplete."));
        }

        if (authority.Installation.Id != request.InstallationId ||
            authority.Installation.Version != request.ExpectedInstallationVersion ||
            authority.Agent.Id != request.AgentId || authority.Agent.InstallationId != request.InstallationId ||
            authority.Agent.Version != request.ExpectedAgentVersion)
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.ConcurrencyConflict,
                "Model route authority versions do not match the requested snapshot."));
        }

        if (authority.Installation.State is not InstallationState.Ready)
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.InvalidStateTransition,
                "Model routes are unavailable unless the installation is Ready."));
        }

        var primaryProfiles = authority.ProviderProfiles
            .Where(item => item.Id == authority.Agent.ModelPolicy.PrimaryProviderProfileId)
            .Take(2)
            .ToArray();
        if (primaryProfiles.Length != 1 ||
            primaryProfiles[0].InstallationId != authority.Installation.Id)
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.ConcurrencyConflict,
                "The current durable primary model profile is missing or ambiguous."));
        }

        if (!string.Equals(primaryProfiles[0].Model, preparedRequest.Model, StringComparison.Ordinal))
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.PolicyDenied,
                "The requested model does not match the current durable agent model policy."));
        }

        var budget = authority.Agent.Budget;
        if (request.EstimatedInputTokens > budget.MaxInputTokens ||
            preparedRequest.Limits.MaximumOutputTokens > budget.MaxOutputTokens ||
            preparedRequest.Limits.MaximumToolCalls > budget.MaxToolInvocations ||
            preparedRequest.Limits.MaximumWallClockSeconds > budget.MaxWallClockSeconds)
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.BudgetExceeded,
                "The model request exceeds the current durable agent budget."));
        }

        return DomainResult.Success(true);
    }

    private static DomainResult<ModelRouteAuthoritySnapshot> SnapshotAuthority(
        ModelRouteAuthoritySnapshot authority)
    {
        if (authority is null || authority.Installation is null || authority.Agent is null ||
            authority.ProviderProfiles is null || authority.Agent.ModelPolicy is null ||
            authority.Agent.MemoryPolicy is null || authority.Agent.CapabilityPolicy is null ||
            authority.Agent.CapabilityPolicy.ToolGrants is null ||
            authority.Agent.CapabilityPolicy.SkillGrants is null || authority.Agent.Budget is null ||
            authority.Agent.ChildLimits is null || authority.Agent.LearningPolicy is null)
        {
            return DomainResult.Fail<ModelRouteAuthoritySnapshot>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Model route authority snapshot is incomplete."));
        }

        var profiles = new List<ProviderProfile>(authority.ProviderProfiles.Count);
        foreach (var profile in authority.ProviderProfiles)
        {
            if (profile is null || profile.SecretReference is null || profile.Capabilities is null)
            {
                return DomainResult.Fail<ModelRouteAuthoritySnapshot>(new DomainFailure(
                    FailureCode.ValidationFailure,
                    "Model route authority provider profiles are incomplete."));
            }

            profiles.Add(profile with
            {
                SecretReference = profile.SecretReference with { },
                Capabilities = profile.Capabilities with { },
            });
        }

        var agent = authority.Agent with
        {
            ModelPolicy = authority.Agent.ModelPolicy with { },
            MemoryPolicy = authority.Agent.MemoryPolicy with { },
            CapabilityPolicy = authority.Agent.CapabilityPolicy with
            {
                ToolGrants = Array.AsReadOnly(authority.Agent.CapabilityPolicy.ToolGrants.ToArray()),
                SkillGrants = Array.AsReadOnly(authority.Agent.CapabilityPolicy.SkillGrants.ToArray()),
            },
            Budget = authority.Agent.Budget with { },
            ChildLimits = authority.Agent.ChildLimits with { },
            LearningPolicy = authority.Agent.LearningPolicy with { },
        };
        return DomainResult.Success(new ModelRouteAuthoritySnapshot(
            authority.Installation with { },
            agent,
            new ReadOnlyCollection<ProviderProfile>(profiles)));
    }

    private static DomainResult<IReadOnlyDictionary<ProviderProfileId, ProviderProfile>> ValidateProfiles(
        ModelRouteAuthoritySnapshot authority,
        IReadOnlyList<ModelProviderDescriptor> candidates,
        DateTimeOffset now)
    {
        if (authority.ProviderProfiles.Count > MaximumCandidateProfiles ||
            authority.ProviderProfiles.Any(item => item is null ||
                item.Id.Value == Guid.Empty || item.InstallationId != authority.Installation.Id) ||
            authority.ProviderProfiles.Select(item => item.Id).Distinct().Count() !=
                authority.ProviderProfiles.Count)
        {
            return ProfileFailure("Durable model provider profiles are invalid, duplicate, or unbounded.");
        }

        var profiles = authority.ProviderProfiles.ToDictionary(item => item.Id);
        foreach (var descriptor in candidates)
        {
            if (!profiles.TryGetValue(descriptor.ProfileId, out var profile) ||
                !string.Equals(profile.ProviderType, descriptor.ProviderType, StringComparison.Ordinal) ||
                !string.Equals(profile.Model, descriptor.Model, StringComparison.Ordinal) ||
                ModelContractValidator.Supports(descriptor, ModelCapability.TextGeneration, now) &&
                    !profile.Capabilities.TextGeneration ||
                ModelContractValidator.Supports(descriptor, ModelCapability.Streaming, now) &&
                    !profile.Capabilities.Streaming ||
                ModelContractValidator.Supports(descriptor, ModelCapability.ToolCalls, now) &&
                    !profile.Capabilities.ToolCalls ||
                ModelContractValidator.Supports(descriptor, ModelCapability.ImageInput, now) &&
                    !profile.Capabilities.Images)
            {
                return ProfileFailure("The immutable model catalog does not match current durable provider profiles.");
            }
        }

        return DomainResult.Success<IReadOnlyDictionary<ProviderProfileId, ProviderProfile>>(
            new ReadOnlyDictionary<ProviderProfileId, ProviderProfile>(profiles));
    }

    private static ReadOnlyCollection<ProviderProfileId> BuildExclusions(
        IReadOnlyList<ModelProviderDescriptor> candidates,
        ModelProviderHealthCatalog health,
        IReadOnlyList<ProviderProfileId> attempted,
        DateTimeOffset now)
    {
        var byProfile = health.List().ToDictionary(item => item.ProfileId);
        var excluded = attempted.ToHashSet();
        foreach (var candidate in candidates)
        {
            if (!byProfile.TryGetValue(candidate.ProfileId, out var evidence) ||
                evidence.Status is not ModelProviderHealthStatus.Healthy ||
                evidence.ObservedAt > now || now >= evidence.ExpiresAt)
            {
                excluded.Add(candidate.ProfileId);
            }
        }

        return Array.AsReadOnly(excluded.OrderBy(item => item.Value).ToArray());
    }

    private static bool PersistedProfileAllows(
        ProviderProfile profile,
        IReadOnlySet<ModelCapability> required) =>
        required.All(capability => capability switch
        {
            ModelCapability.TextGeneration => profile.Capabilities.TextGeneration,
            ModelCapability.Streaming => profile.Capabilities.Streaming,
            ModelCapability.ToolCalls => profile.Capabilities.ToolCalls,
            ModelCapability.ImageInput => profile.Capabilities.Images,
            ModelCapability.StructuredOutput or ModelCapability.AudioInput or
                ModelCapability.DocumentInput => false,
            _ => false,
        });

    private static string ComputeAuthorityHash(
        ModelRouteAuthoritySnapshot authority,
        HashSet<ProviderProfileId> candidateIds)
    {
        var canonical = new
        {
            Installation = new
            {
                Id = authority.Installation.Id.ToString(),
                State = authority.Installation.State.ToString(),
                authority.Installation.Version,
            },
            Agent = new
            {
                Id = authority.Agent.Id.ToString(),
                InstallationId = authority.Agent.InstallationId.ToString(),
                authority.Agent.Version,
                PrimaryProfileId = authority.Agent.ModelPolicy.PrimaryProviderProfileId.ToString(),
                DataLocality = authority.Agent.ModelPolicy.DataLocality.ToString(),
                authority.Agent.ModelPolicy.AllowFallback,
                authority.Agent.Budget.MaxTurns,
                authority.Agent.Budget.MaxToolInvocations,
                authority.Agent.Budget.MaxInputTokens,
                authority.Agent.Budget.MaxOutputTokens,
                authority.Agent.Budget.MaxWallClockSeconds,
            },
            Profiles = authority.ProviderProfiles
                .Where(item => candidateIds.Contains(item.Id))
                .OrderBy(item => item.Id.Value)
                .Select(item => new
                {
                    Id = item.Id.ToString(),
                    InstallationId = item.InstallationId.ToString(),
                    item.ProviderType,
                    Endpoint = item.Endpoint.AbsoluteUri,
                    item.Model,
                    SecretStore = item.SecretReference.Store,
                    SecretKey = item.SecretReference.Key,
                    item.Version,
                    item.Capabilities.TextGeneration,
                    item.Capabilities.Streaming,
                    item.Capabilities.ToolCalls,
                    item.Capabilities.Images,
                    item.Capabilities.EvidenceSource,
                })
                .ToArray(),
        };
        return Hash(canonical);
    }

    private static string ComputeHealthHash(
        IReadOnlyList<ModelProviderDescriptor> candidates,
        ModelProviderHealthCatalog health)
    {
        var byProfile = health.List().ToDictionary(item => item.ProfileId);
        var canonical = candidates
            .OrderBy(item => item.ProfileId.Value)
            .Select(item => byProfile.TryGetValue(item.ProfileId, out var evidence)
                ? new
                {
                    ProfileId = item.ProfileId.ToString(),
                    Status = evidence.Status.ToString(),
                    Source = evidence.Source.ToString(),
                    evidence.ConsecutiveFailures,
                    evidence.EvidenceCode,
                    evidence.ObservedAt,
                    evidence.ExpiresAt,
                    evidence.RetryAfter,
                }
                : new
                {
                    ProfileId = item.ProfileId.ToString(),
                    Status = "Missing",
                    Source = string.Empty,
                    ConsecutiveFailures = 0,
                    EvidenceCode = string.Empty,
                    ObservedAt = default(DateTimeOffset),
                    ExpiresAt = default(DateTimeOffset),
                    RetryAfter = (DateTimeOffset?)null,
                })
            .ToArray();
        return Hash(canonical);
    }

    private static DateTimeOffset ComputeValidUntil(
        DateTimeOffset plannedAt,
        IReadOnlyList<ModelProviderDescriptor> candidates,
        ModelProviderHealthCatalog health)
    {
        var transitions = new List<DateTimeOffset> { plannedAt.Add(MaximumPlanLifetime) };
        foreach (var candidate in candidates)
        {
            AddFuture(transitions, candidate.Routing?.ObservedAt, plannedAt);
            AddFuture(transitions, candidate.Routing?.ExpiresAt, plannedAt);
            foreach (var capability in candidate.Capabilities)
            {
                AddFuture(transitions, capability.ObservedAt, plannedAt);
                AddFuture(transitions, capability.ExpiresAt, plannedAt);
            }
        }

        foreach (var evidence in health.List().Where(item =>
            candidates.Any(candidate => candidate.ProfileId == item.ProfileId)))
        {
            AddFuture(transitions, evidence.ObservedAt, plannedAt);
            AddFuture(transitions, evidence.ExpiresAt, plannedAt);
            AddFuture(transitions, evidence.RetryAfter, plannedAt);
        }

        return transitions.Min();
    }

    private static void AddFuture(
        List<DateTimeOffset> transitions,
        DateTimeOffset? value,
        DateTimeOffset now)
    {
        if (value is { } transition && transition > now)
        {
            transitions.Add(transition);
        }
    }

    private static string ComputePlanHash(
        ModelRoutePlanningRequest request,
        ModelRouteSelection route,
        long providerVersion,
        string preparedInputHash,
        PreparedModelContext prepared,
        string healthHash,
        DateTimeOffset plannedAt,
        DateTimeOffset validUntil) => Hash(new
        {
            RequestId = request.Request.Id.ToString(),
            InstallationId = request.InstallationId.ToString(),
            request.ExpectedInstallationVersion,
            AgentId = request.AgentId.ToString(),
            request.ExpectedAgentVersion,
            RouteProfileId = route.ProfileId.ToString(),
            providerVersion,
            route.SelectionEvidenceHash,
            preparedInputHash,
            prepared.RedactionCount,
            prepared.Policy,
            healthHash,
            plannedAt,
            validUntil,
        });

    private static string Hash<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    }

    private static DomainResult<IReadOnlyDictionary<ProviderProfileId, ProviderProfile>> ProfileFailure(
        string message) =>
        DomainResult.Fail<IReadOnlyDictionary<ProviderProfileId, ProviderProfile>>(new DomainFailure(
            FailureCode.ConcurrencyConflict,
            message));

    private static DomainResult<ModelRoutePlan> Failure(
        FailureCode code,
        string message,
        bool retryable = false) =>
        DomainResult.Fail<ModelRoutePlan>(new DomainFailure(code, message, retryable));
}
