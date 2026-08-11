using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Models;

public sealed record ModelRunExecutionRequest(
    ModelRunId RunId,
    long ExpectedRunVersion,
    ModelRequest Request,
    ActorId ActorId,
    string WorkerId,
    CorrelationId CorrelationId,
    CorrelationId? CausationId = null);

public sealed record ModelRunExecutionResult(ModelRunAggregate Aggregate);
