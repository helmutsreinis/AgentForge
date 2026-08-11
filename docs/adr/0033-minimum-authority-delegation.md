# ADR 0033: Derive child authority by explicit intersection

Status: Accepted

## Context

Delegated agents must not inherit a parent's full context or turn a requested capability/budget into
new authority. Depth, total children, and concurrent children are separate exhaustion dimensions.

## Decision

Create a child grant only from trusted parent authority and explicit child intent. Pass only the
requested context hashes that already exist in the parent's allowed evidence set. Intersect
requested capabilities with the parent set; deny if any declared required capability is missing.
Clamp every budget dimension to requested, per-child, and remaining-parent minima.

Fail when depth, total-child, concurrency, expiry, or positive execution budget is exhausted. Pin
the parent's policy and skill snapshot hashes. Canonically hash and durably store each immutable
grant with its exact parent/child identities and redacted audit evidence.

## Consequences

Delegation is deterministic, reviewable, and cannot expand authority. Callers must explicitly
select minimum context and distinguish optional from required capabilities.
