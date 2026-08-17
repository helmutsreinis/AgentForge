using System.Security.Cryptography;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;

namespace AgentForge.Domain.Tools;

public readonly record struct ToolInvocationId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public enum ToolParameterValueKind
{
    Text,
    WholeNumber,
    Switch,
}

public sealed record ToolParameterValue(
    ToolParameterValueKind Kind,
    string? Text,
    long? WholeNumber,
    bool? Switch);

public enum ToolInvocationState
{
    Authorized,
    Succeeded,
    ToolFailed,
    ExecutionFailed,
    Canceled,
}

public sealed record ToolInvocationRequest(
    long ExpectedInstallationVersion,
    AgentIdentityId AgentId,
    long AgentVersion,
    ActorId ActorId,
    string ToolId,
    string ToolVersion,
    IReadOnlyDictionary<string, ToolParameterValue> Parameters,
    string Workspace,
    string IdempotencyKey,
    CorrelationId CorrelationId,
    CorrelationId? CausationId = null);

public sealed record ToolInvocationPlanRequest(
    long ExpectedInstallationVersion,
    AgentIdentityId AgentId,
    long AgentVersion,
    ActorId ActorId,
    string ToolId,
    string ToolVersion,
    IReadOnlyDictionary<string, ToolParameterValue> Parameters,
    string Workspace,
    CorrelationId CorrelationId,
    CorrelationId? CausationId = null);

public sealed record ToolInvocationPlan(
    CapabilityInvocationRequest Invocation,
    AuthorizationContext Authorization,
    ToolDescriptor Descriptor,
    IReadOnlyList<string> Arguments);

public sealed record BuiltInToolExecutionRequest(
    string HandlerId,
    IReadOnlyDictionary<string, ToolParameterValue> Parameters,
    string Workspace,
    string? Target,
    int MaximumOutputBytes);

public sealed record ToolInvocationRecord(
    ToolInvocationId Id,
    InstallationId InstallationId,
    long InstallationVersion,
    AgentIdentityId AgentId,
    long AgentVersion,
    ActorId ActorId,
    string ToolId,
    string ToolVersion,
    string ToolDescriptorHash,
    string CapabilityId,
    CapabilityRiskClass RiskClass,
    string ParametersHash,
    AuthorizationTargetKind TargetKind,
    string TargetHash,
    string WorkspaceHash,
    string RequestHash,
    CapabilityApprovalId? ApprovalId,
    ToolInvocationState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string IdempotencyKey,
    CorrelationId CorrelationId,
    CorrelationId? CausationId,
    int? ExitCode,
    string? StandardOutputHash,
    int StandardOutputLength,
    string? StandardErrorHash,
    int StandardErrorLength,
    FailureCode? FailureCode,
    long Version);

public sealed record ToolInvocationResult(
    ToolInvocationRecord Invocation,
    bool IsIdempotentReplay,
    byte[] StandardOutput,
    byte[] StandardError,
    ProcessSandboxCapabilities? Sandbox);

public sealed record ToolAvailabilityProbeRequest(
    long ExpectedInstallationVersion,
    AgentIdentityId AgentId,
    long AgentVersion,
    ActorId ActorId,
    string ToolId,
    string ToolVersion,
    string Workspace,
    string IdempotencyKey,
    CorrelationId CorrelationId,
    CorrelationId? CausationId = null);

public sealed record ToolAvailabilityProbeResult(
    ToolInvocationRecord Invocation,
    bool IsAvailable,
    bool IsIdempotentReplay,
    string? ObservedSummary,
    bool SummaryWasRedacted,
    bool SummaryWasTruncated);

public static class ToolInvocationStateMachine
{
    public static DomainResult<ToolInvocationRecord> Authorize(
        ToolInvocationId id,
        AuthorizationContext context,
        string descriptorHash,
        CapabilityApprovalId? approvalId,
        string idempotencyKey,
        DateTimeOffset authorizedAt)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (id.Value == Guid.Empty || context.ToolId is null || context.ToolVersion is null ||
            context.ToolDescriptorHash is null || !FixedEquals(context.ToolDescriptorHash, descriptorHash) ||
            !IsBounded(idempotencyKey, 256) || !IsSha256(descriptorHash))
        {
            return Invalid("Authorized invocation requires exact tool identity, descriptor hash, and idempotency.");
        }

        return DomainResult.Success(new ToolInvocationRecord(
            id,
            context.InstallationId,
            context.InstallationVersion,
            context.AgentId,
            context.AgentVersion,
            context.ActorId,
            context.ToolId,
            context.ToolVersion,
            descriptorHash,
            context.CapabilityId,
            context.RiskClass,
            context.ParametersHash,
            context.TargetKind,
            context.TargetHash,
            context.WorkspaceHash,
            context.RequestHash,
            approvalId,
            ToolInvocationState.Authorized,
            authorizedAt,
            null,
            idempotencyKey,
            context.CorrelationId,
            context.CausationId,
            null,
            null,
            0,
            null,
            0,
            null,
            0));
    }

    public static DomainResult<ToolInvocationRecord> Complete(
        ToolInvocationRecord invocation,
        ProcessExecutionResult execution)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(execution);
        if (invocation.State is not ToolInvocationState.Authorized ||
            execution.CompletedAt < invocation.CreatedAt || execution.StandardOutput is null ||
            execution.StandardError is null)
        {
            return Invalid("Only an authorized invocation can record a valid process completion.");
        }

        return DomainResult.Success(invocation with
        {
            State = execution.ExitCode == 0 ? ToolInvocationState.Succeeded : ToolInvocationState.ToolFailed,
            CompletedAt = execution.CompletedAt,
            ExitCode = execution.ExitCode,
            StandardOutputHash = Hash(execution.StandardOutput),
            StandardOutputLength = execution.StandardOutput.Length,
            StandardErrorHash = Hash(execution.StandardError),
            StandardErrorLength = execution.StandardError.Length,
            Version = checked(invocation.Version + 1),
        });
    }

    public static DomainResult<ToolInvocationRecord> Fail(
        ToolInvocationRecord invocation,
        DomainFailure failure,
        DateTimeOffset failedAt)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(failure);
        if (invocation.State is not ToolInvocationState.Authorized || failedAt < invocation.CreatedAt)
        {
            return Invalid("Only an authorized invocation can record execution failure.");
        }

        return DomainResult.Success(invocation with
        {
            State = ToolInvocationState.ExecutionFailed,
            CompletedAt = failedAt,
            FailureCode = failure.Code,
            Version = checked(invocation.Version + 1),
        });
    }

    public static DomainResult<ToolInvocationRecord> Cancel(
        ToolInvocationRecord invocation,
        DateTimeOffset canceledAt)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (invocation.State is not ToolInvocationState.Authorized || canceledAt < invocation.CreatedAt)
        {
            return Invalid("Only an authorized invocation can be canceled.");
        }

        return DomainResult.Success(invocation with
        {
            State = ToolInvocationState.Canceled,
            CompletedAt = canceledAt,
            Version = checked(invocation.Version + 1),
        });
    }

    private static string Hash(ReadOnlySpan<byte> value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(value))}";

    private static bool FixedEquals(string first, string second) =>
        first.Length == second.Length && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(first),
            System.Text.Encoding.ASCII.GetBytes(second));

    private static bool IsSha256(string value)
    {
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value.AsSpan(7))
        {
            if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);

    private static DomainResult<ToolInvocationRecord> Invalid(string message) =>
        DomainResult.Fail<ToolInvocationRecord>(new DomainFailure(FailureCode.InvalidStateTransition, message));
}
