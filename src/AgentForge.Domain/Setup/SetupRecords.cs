using AgentForge.Domain.Auditing;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Setup;

public sealed record BeginSetupRequest(
    InstallationId? InstallationId,
    ActorId ActorId,
    CorrelationId CorrelationId);

public sealed record BeginSetupResult(
    InstallationSnapshot Installation,
    AuditEventRecord AuditEvent);
