# ADR 0022: Exact Policy and Locality Model Routing

## Status

Accepted on 2026-08-11.

## Context

A provider catalog can contain several exact profiles for one model. Selecting one from a
model-controlled identifier or from declared capabilities alone could bypass the agent's
primary-profile policy, send local-only content to a cloud destination, use stale evidence,
drop media, exceed a context window, or make failover nondeterministic. Provider health and
durable policy/profile reads are later application-service concerns, so the first routing
primitive must remain pure, immutable, and unavailable as a public invocation surface.

## Decision

Add `IModelRouter` as a synchronous harness-owned contract over immutable catalog snapshots.
The request carries the provider-neutral model request, the effective agent model policy,
bounded input estimate, and exact profiles excluded by the current attempt. Descriptors may
carry bounded routing evidence for data location, policy approval, context/output limits,
reliability, cost, latency, observation time, and expiry.

Route in a fixed fail-closed sequence: exact model, attempt exclusions, current modality
evidence, data locality, current policy-approved routing evidence, context/output bounds,
then tool support. A viable exact primary always wins. Fallback must be enabled and ranks
remaining profiles by reliability descending, known combined cost ascending, typical latency
ascending, then profile ID. Missing, future, expired, opposed, malformed, or unapproved
evidence never authorizes a route. Image/audio/document references contribute required
capabilities and are either preserved for a capable route or rejected with a typed failure.

Return an immutable selection with required capabilities and a canonical SHA-256 evidence
hash bound to policy, input/output context requirements, exclusions, the selected profile,
fallback state, and provider evidence. The production catalog remains empty and no API/CLI
route is added.

## Consequences

- Local-only requests cannot select descriptors marked as cloud, including during fallback.
- Primary selection and fallback ordering are reproducible across Windows and Linux.
- Route evidence is suitable for later audit/run snapshots but is not itself authorization.
- A later invocation service must re-read the current durable agent/profile, add bounded
  health and destination evidence, persist the decision, enforce cumulative budgets, and
  resolve the selected adapter immediately before egress.
- Until that service is gated, production composition has no provider and no callable model
  surface.
