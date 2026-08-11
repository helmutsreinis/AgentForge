# ADR 0025: Durable Model Attempt Execution

## Status

Accepted on 2026-08-11.

## Context

A durable `Reserved` run is idempotent admission evidence, not permission to contact a provider.
Starting directly from that row could overcommit an agent across concurrent runs, race changed
installation/provider policy, let two workers claim the same attempt, lose cancellation and usage
evidence, or persist unbounded provider content. A crash between an external call and durable start
evidence would also make replay safety unknowable.

## Decision

Add an internal `IModelRunExecutionService`. It re-applies context preparation, re-plans using the
persisted attempted-profile set, requires exact route/input/health/reservation agreement, re-reads
the Ready installation and exact agent versions, and resolves only the selected profile from the
immutable provider catalog. Tool-bearing requests remain unsupported at this boundary.

Before enumerating the provider, one transaction changes the run/attempt to `Running`/`Started`,
adds or updates an optimistic-concurrency agent budget ledger, and appends `model.run-started`.
The lease uses 256 random bits; only its SHA-256 is persisted, while the exact base64url token stays
inside the invocation and is required for every running terminal transition. Shared reservations
cover input/output tokens, tool calls, events, and wall time. Active reservations pin the agent
version; an idle ledger can advance versions without losing lifetime consumption.

An invocation-local accumulator accepts only the exact request, profile/type/model, prepared-input
hash, contiguous sequence, monotonic lease-bounded timestamps, bounded typed content, at most one
usage event, and exactly one terminal event. Tool calls are rejected. It hashes length-prefixed
canonical events and persists only count, last sequence, SHA-256, normalized usage, terminal state,
and safe classification. Prompt, deltas, structured output, and provider error text have no
persistence path. Success, malformed stream, provider failure, budget exhaustion, or caller
cancellation atomically update the run/attempt, release the exact ledger reservation, accumulate
consumption, and append terminal audit evidence. Caller cancellation is rethrown only after that
terminal transaction succeeds.

Production still registers empty provider and health catalogs and exposes no host or CLI model
mutation. Migration 0010 adds execution evidence and safe defaults for earlier reserved rows.

## Consequences

- A committed start proves exactly one worker held the attempt lease before provider enumeration.
- Concurrent runs cannot reserve beyond the current agent token/tool/time budget.
- Hostile, truncated, reordered, late, tool-bearing, or over-budget streams cannot become success.
- Exact replay of a terminal run fails before resolving or invoking the adapter.
- Raw model context and output remain transient even when an observer consumes typed events.
- A process crash after start can still leave `Running` plus an active ledger reservation. Heartbeat,
  expired-lease takeover/reconciliation, additional attempts, health recording, and failover are the
  next gate; operators must preserve rather than delete that evidence.
- Hosted destination/DNS revalidation remains required before production catalog composition.
