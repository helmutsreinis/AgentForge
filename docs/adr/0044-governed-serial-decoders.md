# ADR 0044: Governed declarative serial decoders

Status: Accepted

## Decision

Learn protocols first through a bounded, non-executable fixed-frame decoder definition. Bind sync bytes, frame length,
typed non-overlapping fields, decode-only authority, semantic version, and canonical definition hash. Preserve unclaimed
frame bytes and unframed noise/partial tails exactly, and retain a raw-frame hash.

Bind evaluations to an exact suite hash and require target, holdout, malformed, partial, concatenated, resynchronization,
unknown-preservation, deterministic fuzz, and operation-bound evidence. Persist proposal transitions as an append-only hash
chain. Require distinct proposer, evaluator, approver, and governor actors; exact active-baseline comparison; passing canary;
atomic active-pointer update; quarantine; and exact-hash rollback.

## Consequences

Compiled decoder plugins remain outside this gate. A competing candidate may finish evaluation but cannot promote after its
baseline becomes stale. Failed canaries are durable quarantine evidence and do not become active. No decoder proposal can
acquire capture, read, write, firmware, filesystem, or network authority.
