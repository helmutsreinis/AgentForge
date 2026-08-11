# ADR 0032: Own durable DAG orchestration as hash-chained snapshots

Status: Accepted

## Context

Agent work must survive process loss, bound concurrency and retries, prevent completed-node replay,
and support compensation without delegating durable authority to a model or optional workflow SDK.

## Decision

Represent each task as an immutable validated DAG with exact installation/agent authority, pinned
policy/budget/skill hashes, typed node budgets, capability/context evidence, retry bounds, and
optional unique compensation nodes. Every transition appends a canonical hash-chained snapshot.

Workers receive a random lease token once; only its SHA-256 hash is persisted. Claim, heartbeat,
completion, failure, cancellation, and expired recovery require an exact expected task version.
Completed nodes never return to a claimable state. Snapshot and redacted audit append atomically.

## Consequences

Recovery is deterministic and portable across workers and restarts. SQLite uniqueness provides the
final concurrent-writer boundary. Snapshot JSON increases storage use, but preserves complete
versioned recovery evidence and avoids partial aggregate reconstruction.
