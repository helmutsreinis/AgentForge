using System.Text;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Tools;

namespace AgentForge.Tools;

internal sealed class ToolAvailabilityProbeService(
    IToolCatalog catalog,
    IToolInvocationService invocations,
    ISensitiveDataRedactor redactor) : IToolAvailabilityProbeService
{
    private const int MaximumSummaryCharacters = 512;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<DomainResult<ToolAvailabilityProbeResult>> ProbeAsync(
        ToolAvailabilityProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var described = await catalog.DescribeAsync(request.ToolId, request.ToolVersion, cancellationToken);
        if (!described.IsSuccess)
        {
            return DomainResult.Fail<ToolAvailabilityProbeResult>(described.Failure!);
        }

        if (described.Value.Definition.OperationKind is not ToolOperationKind.AvailabilityProbe)
        {
            return DomainResult.Fail<ToolAvailabilityProbeResult>(new DomainFailure(
                FailureCode.UnsupportedCapability,
                "The exact catalog entry is not an admitted availability probe."));
        }

        var invocation = await invocations.InvokeAsync(new ToolInvocationRequest(
            request.ExpectedInstallationVersion,
            request.AgentId,
            request.AgentVersion,
            request.ActorId,
            request.ToolId,
            request.ToolVersion,
            new Dictionary<string, ToolParameterValue>(StringComparer.Ordinal),
            request.Workspace,
            request.IdempotencyKey,
            request.CorrelationId,
            request.CausationId), null, cancellationToken);
        if (!invocation.IsSuccess)
        {
            return DomainResult.Fail<ToolAvailabilityProbeResult>(invocation.Failure!);
        }

        var summary = ExtractSummary(invocation.Value);
        return DomainResult.Success(new ToolAvailabilityProbeResult(
            invocation.Value.Invocation,
            invocation.Value.Invocation.State is ToolInvocationState.Succeeded,
            invocation.Value.IsIdempotentReplay,
            summary.Text,
            summary.WasRedacted,
            summary.WasTruncated));
    }

    private Summary ExtractSummary(ToolInvocationResult result)
    {
        if (result.IsIdempotentReplay)
        {
            return new Summary(null, false, false);
        }

        var bytes = result.StandardOutput.Length > 0 ? result.StandardOutput : result.StandardError;
        if (bytes.Length == 0)
        {
            return new Summary(null, false, false);
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return new Summary(null, false, false);
        }

        var line = text.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(line) || line.Any(char.IsControl))
        {
            return new Summary(null, false, false);
        }

        var truncated = line.Length > MaximumSummaryCharacters;
        try
        {
            if (redactor.Redact(line).ContainsRedactions)
            {
                return new Summary(null, true, truncated);
            }
        }
        catch (ArgumentException)
        {
            return new Summary(null, true, truncated);
        }

        if (truncated)
        {
            line = line[..MaximumSummaryCharacters];
        }

        return new Summary(line, false, truncated);
    }

    private sealed record Summary(string? Text, bool WasRedacted, bool WasTruncated);
}
