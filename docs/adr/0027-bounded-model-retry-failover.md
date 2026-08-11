# ADR 0027: Bounded Model Retry and Failover

## Status

Accepted on 2026-08-11.

## Context

A retryable provider failure is not permission to repeat an unbounded call or silently change data
locality. The original single-attempt aggregate could preserve one failure but could not retain
prior attempt rows, reserve a total retry budget, exclude the failed profile, prove fallback policy,
or reconcile usage and cost across attempts.

## Decision

Admission accepts an explicit one-to-eight attempt limit, binds it into idempotency identity, and
multiplies the per-attempt route reservation into an immutable total run reservation. Admission
re-reads exact agent authority and rejects a total attempt/token/tool/wall budget above current
agent limits. Each attempt persists its own reservation, route, plan hash, stream evidence, usage,
state, and version; the run points to the latest attempt while prior rows remain append-only.

After a retryable external failure commits run/attempt, ledger release, observed health, and audit,
execution may plan one next attempt. Planning supplies the exact ordered prior-profile IDs as
exclusions and repeats current authority, health, capability, locality, and fallback policy. The
pure retry transition requires contiguous sequence, unique exact history, remaining total budget,
fresh plan evidence, and a different selected profile. Appending the new attempt and
`model.run-retry-planned` audit is atomic before the next lease can start.

Run usage, cost/currency, stream count/hash, and wall time accumulate across attempts. The shared
agent ledger reserves and reconciles only the current attempt, so sequential failover does not
double-reserve the immutable total run budget. Incompatible currencies, total overage, attempt
overage, history substitution, stale versions, unavailable fallback, and exhausted attempt limits
fail closed. Failure to find an authorized fallback leaves the already durable failed attempt as
the final result; it never weakens policy.

Migration 0012 backfills existing single attempts from their run reservation and assigns a maximum
of one. Production still has an empty provider catalog and no public model invocation.

## Consequences

- A retry can contact each exact profile at most once and at most eight profiles overall.
- Locality and `AllowFallback=false` remain stronger than a caller's retry budget.
- Current aggregate reads return the latest attempt; repository history reads return all attempts
  in sequence without reconstructing deleted state.
- A crash after a failed terminal commit but before retry append leaves a safe durable failure. A
  future durable loop/resume service may decide whether to continue from that evidence.
- The typed agent loop, immutable run snapshots, no-progress detection, and structured-output repair
  remain the next Milestone 3 gate.
