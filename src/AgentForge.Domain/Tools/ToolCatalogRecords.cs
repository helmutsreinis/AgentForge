using AgentForge.Domain.Security;

namespace AgentForge.Domain.Tools;

[Flags]
public enum ToolSideEffectKind
{
    None = 0,
    ReadsFileSystem = 1 << 0,
    WritesFileSystem = 1 << 1,
    ReadsNetwork = 1 << 2,
    ExternalMutation = 1 << 3,
    CredentialAccess = 1 << 4,
    PrivilegedOperation = 1 << 5,
    DestructiveOperation = 1 << 6,
    PhysicalControl = 1 << 7,
}

public enum ToolCatalogSourceKind
{
    BuiltIn,
    OperatorConfigured,
    SignatureVerifiedPlugin,
}

public enum ToolTrustLevel
{
    BuiltIn,
    OperatorApproved,
    SignatureVerified,
}

public enum ToolParameterType
{
    Text,
    WholeNumber,
    Switch,
}

public enum ToolArgumentBindingKind
{
    Literal,
    Positional,
    NamedValue,
    BooleanSwitch,
}

public enum ToolOutputSensitivity
{
    Public,
    LocalMetadata,
    PotentiallySensitive,
}

public enum ToolOperationKind
{
    Invocation,
    AvailabilityProbe,
}

public enum ToolExecutionKind
{
    Process,
    BuiltIn,
}

public sealed record ToolProvenance(
    ToolCatalogSourceKind SourceKind,
    ToolTrustLevel TrustLevel,
    string SourceId,
    string SourceVersion,
    string EvidenceHash);

public sealed record ToolParameterDescriptor(
    string Name,
    ToolParameterType Type,
    bool Required,
    int MaximumLength,
    long? MinimumInteger,
    long? MaximumInteger,
    IReadOnlyList<string> AllowedValues,
    string Description);

public sealed record ToolArgumentBinding(
    ToolArgumentBindingKind Kind,
    string? ParameterName,
    string? Token);

public sealed record ToolProcessDefinition(
    string ExecutablePath,
    IReadOnlyList<string> FixedArguments,
    IReadOnlyList<ToolArgumentBinding> ArgumentBindings,
    IReadOnlyList<string> AllowedEnvironmentVariables,
    ProcessSandboxKind RequiredSandbox,
    ProcessNetworkPolicy NetworkPolicy,
    ProcessIsolationFeature RequiredFeatures,
    int TimeoutSeconds,
    int MaximumOutputBytes);

public sealed record ToolDescriptorDefinition(
    string Id,
    string Version,
    string Name,
    string Summary,
    string Description,
    string CapabilityId,
    CapabilityRiskClass RiskClass,
    AuthorizationTargetKind TargetKind,
    string? TargetParameterName,
    ToolSideEffectKind SideEffects,
    ToolOutputSensitivity OutputSensitivity,
    IReadOnlyList<ToolParameterDescriptor> Parameters,
    ToolProcessDefinition Process,
    ToolProvenance Provenance,
    ToolOperationKind OperationKind = ToolOperationKind.Invocation,
    ToolExecutionKind ExecutionKind = ToolExecutionKind.Process,
    string? BuiltInHandlerId = null);

public sealed record ToolDescriptor(
    ToolDescriptorDefinition Definition,
    string DescriptorHash);

public sealed record ToolSummary(
    string Id,
    string Version,
    string Name,
    string Summary,
    string CapabilityId,
    CapabilityRiskClass RiskClass,
    AuthorizationTargetKind TargetKind,
    ToolSideEffectKind SideEffects,
    ToolCatalogSourceKind SourceKind,
    ToolTrustLevel TrustLevel,
    ToolOperationKind OperationKind,
    string DescriptorHash);

public sealed record ToolSearchRequest(
    string Query,
    string? CapabilityId,
    CapabilityRiskClass? MaximumRiskClass,
    int MaximumResults = 10);
