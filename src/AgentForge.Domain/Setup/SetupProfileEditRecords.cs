using AgentForge.Domain.Agents;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;

namespace AgentForge.Domain.Setup;

public sealed record SetupProfileChange(
    string Path,
    string? Before,
    string? After);

public sealed record PreviewProviderEditRequest(
    ProviderProfileId ProviderProfileId,
    long ExpectedInstallationVersion,
    long ExpectedProviderVersion,
    ProviderProfileCandidate Candidate,
    ActorId ActorId,
    CorrelationId CorrelationId,
    ReadOnlyMemory<char> AdministratorCredential);

public sealed record ProviderEditPreview(
    ProviderProfile Current,
    ProviderProfile Effective,
    IReadOnlyList<SetupProfileChange> Changes,
    string RequestHash);

public sealed record ApplyProviderEditRequest(
    ProviderProfileId ProviderProfileId,
    long ExpectedInstallationVersion,
    long ExpectedProviderVersion,
    ProviderProfileCandidate Candidate,
    string ExpectedRequestHash,
    ActorId ActorId,
    CorrelationId CorrelationId,
    ReadOnlyMemory<char> AdministratorCredential);

public sealed record ProviderEditResult(
    InstallationSnapshot Installation,
    ProviderProfile Provider,
    IReadOnlyList<SetupProfileChange> Changes,
    string RequestHash);

public sealed record PreviewAgentEditRequest(
    AgentIdentityId AgentIdentityId,
    long ExpectedInstallationVersion,
    long ExpectedAgentVersion,
    AgentIdentityCandidate Candidate,
    ActorId ActorId,
    CorrelationId CorrelationId,
    ReadOnlyMemory<char> AdministratorCredential);

public sealed record AgentEditPreview(
    AgentIdentity Current,
    EffectiveAgentDefinition Effective,
    IReadOnlyList<SetupProfileChange> Changes,
    string RequestHash);

public sealed record ApplyAgentEditRequest(
    AgentIdentityId AgentIdentityId,
    long ExpectedInstallationVersion,
    long ExpectedAgentVersion,
    AgentIdentityCandidate Candidate,
    string ExpectedRequestHash,
    ActorId ActorId,
    CorrelationId CorrelationId,
    ReadOnlyMemory<char> AdministratorCredential);

public sealed record AgentEditResult(
    InstallationSnapshot Installation,
    AgentIdentity Agent,
    EffectiveAgentDefinition Effective,
    IReadOnlyList<SetupProfileChange> Changes,
    string RequestHash);
