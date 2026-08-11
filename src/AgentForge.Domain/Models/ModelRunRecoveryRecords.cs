using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Models;

public sealed record ModelRunHeartbeatRequest(
    ModelRunId RunId,
    long ExpectedRunVersion,
    long ExpectedAttemptVersion,
    string WorkerId,
    string LeaseToken,
    CorrelationId CorrelationId,
    CorrelationId? CausationId = null);

public sealed record ModelRunRecoveryRequest(
    ModelRunId RunId,
    long ExpectedRunVersion,
    long ExpectedAttemptVersion,
    ActorId ActorId,
    CorrelationId CorrelationId,
    CorrelationId? CausationId = null);

public sealed record ModelRunHeartbeatResult(ModelRunAggregate Aggregate);

public sealed record ModelRunRecoveryResult(
    ModelRunAggregate Aggregate,
    ModelProviderHealthRecord Health);
