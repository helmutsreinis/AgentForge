# ADR 0009: AgentForge owns durable schedule semantics

Status: Accepted

The scheduler persists typed definitions and computes occurrences using explicit
timezone data and a virtualizable clock. LLMs translate intent only; they never
decide actual trigger time. Occurrences carry idempotency keys and pinned agent,
policy, capability, budget, and skill/bundle selection.
