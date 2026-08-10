# Microsoft Agent Framework compatibility spike

This spike is deliberately outside production modules. It verifies that the pinned
framework can construct typed workflows, expose streamed events, and propagate
cancellation without binding AgentForge domain contracts to framework types.

## Decision

Agent Framework may be added later as an optional `IAgentRuntime`/workflow adapter.
It does not own AgentForge tasks, leases, checkpoints, policy, audit, approvals,
skill snapshots, or promotion state. Human-in-the-loop and checkpoint integration
must pass separate persistence-bound contract tests before that adapter is enabled.

The superstep execution model is useful for bounded workflow execution but is not a
replacement for the durable task engine because AgentForge also requires independent
leases, idempotency, compensation, policy snapshots, and cross-process recovery.
