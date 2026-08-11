# ADR 0026: Model Run Recovery and Durable Provider Health

## Status

Accepted on 2026-08-11.

## Context

A committed model start can outlive its worker. Deleting the `Running` row, clearing its lease, or
manually decrementing the shared ledger would lose evidence and could authorize a duplicate remote
call. Treating a failed provider as immediately healthy would also route new work back to a known
bad destination. Recovery must be deterministic, version-bound, redacted, and atomic with budget
release; health must remain bounded evidence rather than mutable operator folklore.

## Decision

Add pure heartbeat and expired-lease transitions. A heartbeat requires the exact owner and raw
lease token, compares the persisted token hash in fixed time, advances time monotonically, and may
not extend expiry. Only the run version changes. The raw token remains invocation memory and has no
audit or database field.

An expired lease may transition only from `Running`/`Started` at or after the persisted expiry. It
becomes a retryable `RecoverableExternalFailure` whose completion time is the exact expiry, retains
the last persisted usage/stream evidence, and keeps the lease hash/times as provenance. The
internal `IModelRunRecoveryService` requires exact run/attempt versions. One transaction updates the
run and attempt, reconciles the active agent reservation, writes observed provider health, and
appends `model.run-lease-expired`. Duplicate or early recovery writes nothing.

Store at most one optimistic-concurrency health row per provider profile. Successful execution
records five-minute `Healthy` evidence and resets failures. Retryable execution failure or lease
expiry records 15-minute `TemporarilyUnavailable` evidence with a five-second exponential retry
window capped at five minutes and a failure count capped at 1,000. Cancellation, policy denial,
unsupported capability, and budget exhaustion do not make a provider-health assertion. The route
planner reads this scoped SQLite source by default; tests and embedding applications may still
replace the source explicitly.

Migration 0011 creates only the health table and fabricates no observations for prior runs.
Production provider composition and all public model invocation/recovery surfaces remain closed.

## Consequences

- A crash no longer requires destructive repair to release an expired budget reservation.
- Lease possession cannot be inferred from worker name, correlation, or database access.
- Health observations have exact run/attempt provenance and race through numeric versions.
- A health-write race rolls back the terminal run and ledger transaction rather than losing or
  overwriting evidence.
- There is no background scanner or operator command yet. Scheduling the internal recovery service
  belongs with the later durable worker/scheduling gate.
- Retry/failover still needs a multi-attempt aggregate and cross-attempt usage/cost accounting.
