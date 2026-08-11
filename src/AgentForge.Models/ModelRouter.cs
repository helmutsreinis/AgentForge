using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;

namespace AgentForge.Models;

public sealed class ModelRouter(
    IModelProviderCatalog catalog,
    IClock clock) : IModelRouter
{
    private const int MaximumExcludedProfiles = 256;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public DomainResult<ModelRouteSelection> SelectRoute(ModelRoutingRequest request)
    {
        var validation = ValidateRequest(request);
        if (!validation.IsSuccess)
        {
            return DomainResult.Fail<ModelRouteSelection>(validation.Failure!);
        }

        var required = GetRequiredCapabilities(request.Request);
        if (!required.IsSuccess)
        {
            return DomainResult.Fail<ModelRouteSelection>(required.Failure!);
        }

        var now = clock.UtcNow;
        var excluded = request.ExcludedProfileIds.ToHashSet();
        var matchingModel = catalog.List()
            .Where(item => string.Equals(item.Model, request.Request.Model, StringComparison.Ordinal))
            .ToArray();
        if (matchingModel.Length == 0)
        {
            return Failure(FailureCode.UnsupportedCapability, "No exact model route is available.");
        }

        var candidates = matchingModel
            .Where(item => !excluded.Contains(item.ProfileId))
            .ToArray();
        if (candidates.Length == 0)
        {
            return Failure(FailureCode.RecoverableExternalFailure, "All exact model routes are excluded for this attempt.", true);
        }

        var modalityCapabilities = required.Value
            .Where(item => item is not ModelCapability.ToolCalls)
            .ToHashSet();
        candidates = candidates
            .Where(item => modalityCapabilities.All(capability =>
                ModelContractValidator.Supports(item, capability, now)))
            .ToArray();
        if (candidates.Length == 0)
        {
            return Failure(FailureCode.UnsupportedCapability, "No route supports the required model modality.");
        }

        candidates = candidates
            .Where(item => LocalityAllows(request.Policy.DataLocality, item.Routing))
            .ToArray();
        if (candidates.Length == 0)
        {
            return Failure(FailureCode.PolicyDenied, "Data-locality policy denied every capable model route.");
        }

        candidates = candidates
            .Where(item => HasCurrentPolicyApproval(item.Routing, now))
            .ToArray();
        if (candidates.Length == 0)
        {
            return Failure(FailureCode.PolicyDenied, "No capable model route has current policy-approved evidence.");
        }

        candidates = candidates
            .Where(item => FitsContext(item.Routing!, request.EstimatedInputTokens, request.Request.Limits.MaximumOutputTokens))
            .ToArray();
        if (candidates.Length == 0)
        {
            return Failure(FailureCode.BudgetExceeded, "The request exceeds every policy-approved model context window.");
        }

        if (required.Value.Contains(ModelCapability.ToolCalls))
        {
            candidates = candidates
                .Where(item => ModelContractValidator.Supports(item, ModelCapability.ToolCalls, now))
                .ToArray();
            if (candidates.Length == 0)
            {
                return Failure(FailureCode.UnsupportedCapability, "No remaining model route supports the requested tools.");
            }
        }

        var primary = candidates.SingleOrDefault(item => item.ProfileId == request.Policy.PrimaryProviderProfileId);
        ModelProviderDescriptor selected;
        var isFallback = primary is null;
        if (primary is not null)
        {
            selected = primary;
        }
        else if (!request.Policy.AllowFallback)
        {
            return Failure(FailureCode.PolicyDenied, "The exact primary model route is unavailable and fallback is disabled.");
        }
        else
        {
            selected = candidates
                .Where(item => item.ProfileId != request.Policy.PrimaryProviderProfileId)
                .OrderByDescending(item => item.Routing!.ReliabilityBasisPoints)
                .ThenBy(item => CombinedCost(item.Routing!))
                .ThenBy(item => item.Routing!.TypicalLatencyMilliseconds)
                .ThenBy(item => item.ProfileId.Value)
                .FirstOrDefault()!;
            if (selected is null)
            {
                return Failure(FailureCode.RecoverableExternalFailure, "No approved fallback model route is available.", true);
            }
        }

        var normalized = ModelContractValidator.NormalizeRequest(request.Request, selected, now);
        if (!normalized.IsSuccess)
        {
            return DomainResult.Fail<ModelRouteSelection>(normalized.Failure!);
        }

        var requiredSnapshot = new ReadOnlySet<ModelCapability>(required.Value);
        return DomainResult.Success(new ModelRouteSelection(
            selected.ProfileId,
            selected.ProviderType,
            selected.Model,
            isFallback,
            requiredSnapshot,
            ComputeSelectionEvidenceHash(request, selected, requiredSnapshot, isFallback)));
    }

    private static DomainResult<bool> ValidateRequest(ModelRoutingRequest request)
    {
        if (request is null || request.Request is null || request.Policy is null ||
            request.Policy.PrimaryProviderProfileId.Value == Guid.Empty ||
            !Enum.IsDefined(request.Policy.DataLocality) ||
            request.EstimatedInputTokens is < 0 or > 10_000_000 ||
            request.ExcludedProfileIds is null || request.ExcludedProfileIds.Count > MaximumExcludedProfiles ||
            request.ExcludedProfileIds.Any(item => item.Value == Guid.Empty) ||
            request.ExcludedProfileIds.Distinct().Count() != request.ExcludedProfileIds.Count ||
            string.IsNullOrWhiteSpace(request.Request.Model) || request.Request.Messages is null ||
            request.Request.Tools is null || request.Request.ResponseFormat is null || request.Request.Limits is null)
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Model routing identity, policy, estimates, exclusions, or request contracts are invalid."));
        }

        return DomainResult.Success(true);
    }

    private static DomainResult<HashSet<ModelCapability>> GetRequiredCapabilities(ModelRequest request)
    {
        var required = new HashSet<ModelCapability>
        {
            ModelCapability.TextGeneration,
            ModelCapability.Streaming,
        };
        try
        {
            foreach (var message in request.Messages)
            {
                if (message?.Content is null)
                {
                    return InvalidCapabilities();
                }

                foreach (var content in message.Content)
                {
                    switch (content)
                    {
                        case ModelAttachmentContent attachment when attachment.Attachment is not null:
                            required.Add(attachment.Attachment.Modality switch
                            {
                                ModelAttachmentModality.Image => ModelCapability.ImageInput,
                                ModelAttachmentModality.Audio => ModelCapability.AudioInput,
                                ModelAttachmentModality.Document => ModelCapability.DocumentInput,
                                _ => throw new InvalidOperationException(),
                            });
                            break;
                        case ModelToolCallContent or ModelToolResultContent:
                            required.Add(ModelCapability.ToolCalls);
                            break;
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
            return InvalidCapabilities();
        }

        if (request.Tools.Count > 0)
        {
            required.Add(ModelCapability.ToolCalls);
        }

        if (request.ResponseFormat.Kind is not ModelResponseFormatKind.Text)
        {
            required.Add(ModelCapability.StructuredOutput);
        }

        return DomainResult.Success(required);
    }

    private static bool LocalityAllows(ModelDataLocality locality, ModelProviderRoutingEvidence? evidence) =>
        evidence is not null &&
        (locality is ModelDataLocality.CloudAllowed || evidence.DataLocation is not ModelProviderDataLocation.Cloud);

    private static bool HasCurrentPolicyApproval(ModelProviderRoutingEvidence? evidence, DateTimeOffset now) =>
        evidence is not null && evidence.Source is ModelCapabilityEvidenceSource.PolicyApproved &&
        evidence.ObservedAt <= now && (evidence.ExpiresAt is null || now < evidence.ExpiresAt);

    private static bool FitsContext(
        ModelProviderRoutingEvidence evidence,
        long estimatedInputTokens,
        int requestedOutputTokens) =>
        requestedOutputTokens <= evidence.MaximumOutputTokens &&
        estimatedInputTokens <= evidence.MaximumContextTokens - requestedOutputTokens;

    private static decimal CombinedCost(ModelProviderRoutingEvidence evidence) =>
        evidence.InputCostPerMillionTokens is null || evidence.OutputCostPerMillionTokens is null
            ? decimal.MaxValue
            : evidence.InputCostPerMillionTokens.Value + evidence.OutputCostPerMillionTokens.Value;

    private static string ComputeSelectionEvidenceHash(
        ModelRoutingRequest request,
        ModelProviderDescriptor selected,
        IReadOnlySet<ModelCapability> required,
        bool isFallback)
    {
        var canonical = new
        {
            Model = request.Request.Model,
            PrimaryProfileId = request.Policy.PrimaryProviderProfileId.ToString(),
            DataLocality = request.Policy.DataLocality.ToString(),
            request.Policy.AllowFallback,
            request.EstimatedInputTokens,
            RequestedOutputTokens = request.Request.Limits.MaximumOutputTokens,
            ExcludedProfiles = request.ExcludedProfileIds
                .OrderBy(item => item.Value)
                .Select(item => item.ToString())
                .ToArray(),
            SelectedProfileId = selected.ProfileId.ToString(),
            isFallback,
            RequiredCapabilities = required.OrderBy(item => item).Select(item => item.ToString()).ToArray(),
            ProviderEvidenceHash = ModelContractValidator.ComputeCapabilityEvidenceHash(selected),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, SerializerOptions);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    }

    private static DomainResult<HashSet<ModelCapability>> InvalidCapabilities() =>
        DomainResult.Fail<HashSet<ModelCapability>>(new DomainFailure(
            FailureCode.ValidationFailure,
            "Model routing could not derive capabilities from the request."));

    private static DomainResult<ModelRouteSelection> Failure(
        FailureCode code,
        string message,
        bool retryable = false) =>
        DomainResult.Fail<ModelRouteSelection>(new DomainFailure(code, message, retryable));
}
