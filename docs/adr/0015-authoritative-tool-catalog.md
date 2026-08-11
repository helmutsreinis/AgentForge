# ADR 0015: Authoritative Tool Catalog

## Status

Accepted on 2026-08-11.

## Context

Passive environment inventory proves only that a path was observed. It does not prove
what the executable does, which arguments are safe, what authority it needs, or whether
it may be invoked. Accepting those fields from a model or invocation request would let
untrusted input understate risk, change an executable, or bypass exact approval binding.

Progressive discovery also needs a smaller disclosure surface than invocation. A model
may search names and summaries without receiving executable paths, fixed arguments, or
environment details for every registered tool.

## Decision

AgentForge admits callable tools only as immutable, typed descriptor versions in an
authoritative catalog. Catalog admission validates normalized IDs, complete SemVer 2
versions, provenance/trust evidence, SHA-256 evidence hashes, risk implied by side
effects, output sensitivity, required typed parameters, exact target mapping, argument
bindings, environment names, sandbox/network requirements, and execution bounds.

The catalog snapshots caller-owned collections and calculates a canonical SHA-256 over
the normalized descriptor. `(tool ID, version)` is unique and exact description never
selects a different version.

Search returns bounded `ToolSummary` records only. Summaries intentionally omit process
paths, fixed arguments, bindings, and environment names. Full descriptors require an
exact normalized ID and version. Admission and search never inspect, probe, or execute
the executable path; availability and safe version/help probes are later operations
through the policy-bound restricted executor.

Invocation requests will name a catalog version and provide parameter values only. The
later invocation service must reconstruct capability, risk, target kind, executable,
argument mapping, bounds, provenance, and sandbox requirements from the descriptor and
bind its descriptor hash into authorization and audit evidence.

## Consequences

- Inventory entries cannot become callable merely because a matching filename exists.
- Model-supplied descriptions, risk classes, executable paths, or argument templates
  are never authoritative.
- Multiple immutable versions may coexist, while exact lookup prevents silent upgrades.
- Platform-specific executable paths may produce platform-specific descriptor hashes;
  approvals and run snapshots therefore pin the exact admitted descriptor on that host.
- Plugin signatures and availability probes remain separate gates; the provenance type
  records their evidence but does not implement those future trust mechanisms.
