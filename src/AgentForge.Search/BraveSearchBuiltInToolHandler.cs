using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Search;
using AgentForge.Abstractions.Time;
using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Search;
using AgentForge.Domain.Tools;

namespace AgentForge.Search;

internal sealed class BraveSearchBuiltInToolHandler(
    IResearchService research,
    ISearchProviderProfileRepository profiles,
    IInstallationStateReader installationState,
    IClock clock) : IBuiltInToolHandler
{
    private const string Endpoint = "https://api.search.brave.com/res/v1/web/search";

    public bool CanHandle(string handlerId) => handlerId is "search.brave";

    public async Task<DomainResult<ProcessExecutionResult>> ExecuteAsync(
        BuiltInToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryText(request.Parameters, "query", 512, out var query) ||
            !TryWholeNumber(request.Parameters, "maximumResults", 1, 10, out var maximumResults) ||
            !TryText(request.Parameters, "endpoint", 2048, out var endpoint) ||
            !string.Equals(endpoint, Endpoint, StringComparison.Ordinal) ||
            !string.Equals(request.Target, Endpoint, StringComparison.Ordinal))
        {
            return Invalid("The Brave search request does not match its exact descriptor.");
        }

        var installation = await installationState.ReadAsync(cancellationToken);
        var profile = await profiles.FindAsync(installation.Id, "brave", cancellationToken);
        if (profile is null || !profile.IsEnabled || !string.Equals(
            profile.Endpoint.AbsoluteUri, Endpoint, StringComparison.Ordinal))
        {
            return DomainResult.Fail<ProcessExecutionResult>(new DomainFailure(
                FailureCode.UnsupportedCapability,
                "Brave Search is not configured and enabled."));
        }

        var requestedAt = clock.UtcNow;
        var queryHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(query)))[..24];
        var response = await research.ResearchAsync(new SearchRequest(
            query,
            maximumResults,
            ["brave"],
            "agent-tool",
            "agent-tool",
            $"agent-search:{queryHash}",
            requestedAt,
            TimeSpan.FromMinutes(10))
        {
            ProviderEvidenceHashes = ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("brave", profile.EvidenceHash),
        }, cancellationToken);
        if (!response.IsSuccess)
        {
            return DomainResult.Fail<ProcessExecutionResult>(response.Failure!);
        }

        var output = JsonSerializer.SerializeToUtf8Bytes(new
        {
            query,
            citations = response.Value.Citations.Select(item => new
            {
                id = item.CitationId,
                source = item.Source.AbsoluteUri,
                item.Title,
                excerpt = item.Excerpt,
                item.EvidenceHash,
            }),
            failures = response.Value.ProviderFailures.Select(item => new
            {
                provider = item.ProviderId,
                kind = item.Kind.ToString(),
                item.IsRetryable,
            }),
            response.Value.IsCacheHit,
            response.Value.EvidenceHash,
        });
        if (output.Length > request.MaximumOutputBytes)
        {
            return DomainResult.Fail<ProcessExecutionResult>(new DomainFailure(
                FailureCode.BudgetExceeded,
                "Normalized Brave Search results exceeded the approved output bound."));
        }

        var completedAt = clock.UtcNow < requestedAt ? requestedAt : clock.UtcNow;
        return DomainResult.Success(new ProcessExecutionResult(
            0,
            output,
            [],
            requestedAt,
            completedAt,
            completedAt - requestedAt,
            new ProcessSandboxCapabilities(
                ProcessSandboxKind.BuiltIn,
                true,
                ProcessIsolationFeature.BoundedOutput | ProcessIsolationFeature.NetworkIsolation,
                "Managed Brave handler; one fixed HTTPS endpoint, OS-backed credential, normalized bounded citations.")));
    }

    private static bool TryText(
        IReadOnlyDictionary<string, ToolParameterValue> parameters,
        string name,
        int maximum,
        out string value)
    {
        value = string.Empty;
        if (!parameters.TryGetValue(name, out var parameter) ||
            parameter.Kind is not ToolParameterValueKind.Text ||
            string.IsNullOrWhiteSpace(parameter.Text) || parameter.Text.Length > maximum ||
            parameter.Text.Any(char.IsControl))
        {
            return false;
        }
        value = parameter.Text.Trim();
        return true;
    }

    private static bool TryWholeNumber(
        IReadOnlyDictionary<string, ToolParameterValue> parameters,
        string name,
        int minimum,
        int maximum,
        out int value)
    {
        value = 0;
        if (!parameters.TryGetValue(name, out var parameter) ||
            parameter.Kind is not ToolParameterValueKind.WholeNumber ||
            parameter.WholeNumber is not { } number || number < minimum || number > maximum)
        {
            return false;
        }
        value = checked((int)number);
        return true;
    }

    private static DomainResult<ProcessExecutionResult> Invalid(string message) =>
        DomainResult.Fail<ProcessExecutionResult>(new DomainFailure(FailureCode.ValidationFailure, message));
}
