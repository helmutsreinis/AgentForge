using System.Text.Json;
using AgentForge.Abstractions.HttpApi;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Time;
using AgentForge.Abstractions.Tools;
using AgentForge.Domain.HttpApi;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Tools;

namespace AgentForge.HttpApi;

internal sealed class HttpApiBuiltInToolHandler(
    IHttpApiReadService reads,
    IHttpApiProfileRepository profiles,
    IInstallationStateReader installationState,
    IClock clock) : IBuiltInToolHandler
{
    public bool CanHandle(string handlerId) => handlerId == "http-api.get";

    public async Task<DomainResult<ProcessExecutionResult>> ExecuteAsync(
        BuiltInToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryText(request.Parameters, "profileId", 64, out var profileText) ||
            !TryText(request.Parameters, "relativePath", 2048, out var relativePath) ||
            !TryText(request.Parameters, "queryJson", 8192, out var queryJson) ||
            !TryText(request.Parameters, "endpoint", 2048, out var endpointText) ||
            !TryWholeNumber(request.Parameters, "maximumResponseBytes", 1, 1_048_576, out var maximumBytes) ||
            request.Parameters.Keys.Any(key => key is not (
                "profileId" or "relativePath" or "queryJson" or "maximumResponseBytes" or "endpoint")))
        {
            return Invalid("The generated API skill requested an invalid bounded HTTP GET.");
        }
        var query = ParseQuery(queryJson);
        if (!query.IsSuccess) return DomainResult.Fail<ProcessExecutionResult>(query.Failure!);
        var installation = await installationState.ReadAsync(cancellationToken);
        var profile = await profiles.FindAsync(
            installation.Id, new HttpApiProfileId(profileText), cancellationToken);
        if (profile is null || !profile.IsEnabled)
        {
            return DomainResult.Fail<ProcessExecutionResult>(new DomainFailure(
                FailureCode.UnsupportedCapability, "The selected HTTP API profile is not configured and enabled."));
        }
        var endpoint = HttpApiContract.BuildEndpoint(profile.BaseEndpoint, relativePath, query.Value);
        if (!endpoint.IsSuccess || !string.Equals(endpoint.Value.AbsoluteUri, endpointText, StringComparison.Ordinal) ||
            !string.Equals(request.Target, endpointText, StringComparison.Ordinal))
        {
            return DomainResult.Fail<ProcessExecutionResult>(new DomainFailure(
                FailureCode.PolicyDenied, "The HTTP API request no longer matches its exact approved endpoint."));
        }
        var started = clock.UtcNow;
        var result = await reads.GetAsync(profile, new HttpApiReadRequest(
            relativePath, query.Value, maximumBytes,
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")), cancellationToken);
        if (!result.IsSuccess) return DomainResult.Fail<ProcessExecutionResult>(result.Failure!);
        var output = JsonSerializer.SerializeToUtf8Bytes(new
        {
            profileId = profile.Id.Value,
            profile.DisplayName,
            endpoint = result.Value.Endpoint.AbsoluteUri,
            result.Value.StatusCode,
            result.Value.ContentType,
            result.Value.Body,
            result.Value.EvidenceHash,
        });
        if (output.Length > request.MaximumOutputBytes)
        {
            return Invalid("The normalized HTTP API result exceeded its configured output bound.");
        }
        var completed = clock.UtcNow < started ? started : clock.UtcNow;
        return DomainResult.Success(new ProcessExecutionResult(
            0, output, [], started, completed, completed - started,
            new ProcessSandboxCapabilities(
                ProcessSandboxKind.BuiltIn, true,
                ProcessIsolationFeature.BoundedOutput | ProcessIsolationFeature.NetworkIsolation,
                "Managed generated-skill HTTP GET; configured HTTPS origin, OS-backed bearer token, exact approval, bounded UTF-8 output.")));
    }

    private static DomainResult<IReadOnlyDictionary<string, string>> ParseQuery(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            if (document.RootElement.ValueKind is not JsonValueKind.Object) return InvalidQuery();
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.EnumerateObject())
            {
                var value = item.Value.ValueKind switch
                {
                    JsonValueKind.String => item.Value.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => item.Value.GetRawText(),
                    _ => null,
                };
                if (value is null || !result.TryAdd(item.Name, value)) return InvalidQuery();
            }
            return result.Count <= 32
                ? DomainResult.Success<IReadOnlyDictionary<string, string>>(result)
                : InvalidQuery();
        }
        catch (JsonException)
        {
            return InvalidQuery();
        }
    }

    private static DomainResult<IReadOnlyDictionary<string, string>> InvalidQuery() =>
        DomainResult.Fail<IReadOnlyDictionary<string, string>>(new DomainFailure(
            FailureCode.ValidationFailure, "The HTTP API query must be one bounded object of scalar values."));

    private static bool TryText(IReadOnlyDictionary<string, ToolParameterValue> values, string name, int maximum, out string result)
    {
        result = string.Empty;
        return values.TryGetValue(name, out var value) && value.Kind is ToolParameterValueKind.Text &&
            value.Text is { } text && text.Length is >= 1 && text.Length <= maximum &&
            !text.Any(character => character == '\0' || character == '\r' || character == '\n') &&
            (result = text).Length > 0;
    }

    private static bool TryWholeNumber(
        IReadOnlyDictionary<string, ToolParameterValue> values, string name, int minimum, int maximum, out int result)
    {
        result = 0;
        if (!values.TryGetValue(name, out var value) || value.Kind is not ToolParameterValueKind.WholeNumber ||
            value.WholeNumber is not { } number || number < minimum || number > maximum) return false;
        result = (int)number;
        return true;
    }

    private static DomainResult<ProcessExecutionResult> Invalid(string message) =>
        DomainResult.Fail<ProcessExecutionResult>(new DomainFailure(FailureCode.ValidationFailure, message));
}
