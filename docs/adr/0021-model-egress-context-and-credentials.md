# ADR 0021: Model-Egress Context and Credential Boundary

## Status

Accepted on 2026-08-11.

## Context

Provider-neutral requests can contain credentials in free text, attachment names, tool
arguments/results, or descriptions even when durable provider metadata is clean. Hosted
HTTP adapters additionally need one exact provider secret without allowing raw headers,
long-lived material, profile substitution, or error/log leakage. Redacting schemas or
identities in place could silently alter routing or contract authority.

## Decision

Add `IModelContextPreparer` at the external adapter boundary. It creates an immutable request
snapshot and applies the shared bounded structured redactor to payload-bearing fields.
Sensitive identity/correlation fields and JSON contracts fail closed rather than being
rewritten. The prepared record and started event identify
`agentforge-context-redaction-v1` and the redaction count; the normal input hash is computed
from the prepared request. Preparation errors are fixed typed messages without source text.

Require every OpenAI-compatible construction to receive this preparer. Add a separate hosted
factory that exact-matches a durable profile to its descriptor, HTTPS endpoint, capability
posture, version, configured secret store, and reference. It stores only the reference.
For each stream it materializes a clearable lease after started evidence, accepts only a
bounded visible-ASCII bearer value, calls `SendAsync`, then removes `Authorization` and clears
the lease in `finally` before response events. Missing, malformed, injected, or mismatched
inputs cause no HTTP call. The fixed BCL header API creates transient managed text that cannot
be zeroed; immediate header removal minimizes this residual lifetime.

## Consequences

- External compatible requests can no longer serialize the caller's raw mutable context.
- Redaction activity has explicit stream evidence without persisting original values or
  source hashes that could become equality oracles.
- Hosted credentials never enter model request JSON, events, logs, durable state, or an
  arbitrary header map, and clearable material is invocation-scoped.
- The credential-free LAN gate remains available with the same preparation requirement.
- Production provider composition remains empty. Current-profile reads, setup validation for
  hosted types, routing, destination/DNS policy, audit, budgets, and run snapshots remain
  mandatory before any public invocation surface.
