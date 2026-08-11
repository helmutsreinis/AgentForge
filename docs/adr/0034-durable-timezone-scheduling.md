# ADR 0034: Use pinned timezone recurrence and durable schedule snapshots

Status: Accepted

## Context

Schedules must survive restarts and behave deterministically across timezones, daylight transitions,
misfires, overlap, retries, and concurrent workers. A timer callback alone is not durable evidence.

## Decision

Represent one-shot, UTC interval, bounded five-field cron, and local calendar recurrence as validated
domain data. Pin the exact system timezone ID and policy/capability/budget/skill hashes. Invalid
local times advance to the first valid minute; ambiguous local times select the earlier UTC instant.
Jitter is deterministic from schedule ID and base occurrence.

Persist every schedule transition as a canonical hash-chained snapshot. A bounded background scan
selects only latest due versions. Misfire is explicit (`Skip`, `FireOnce`, `CatchUp`); overlap is
explicit (`Skip`, `Queue`, `Parallel`). Occurrences have derived idempotency hashes and short
hash-only worker leases. Pause/resume, run-now, expiration, retries, recovery, and dead letter are
state-machine transitions with exact expected versions.

## Consequences

Restart behavior can be replayed and tested without wall-clock sleeps. The cron grammar is
deliberately bounded: lists, ranges, and wildcards only; unsupported extensions fail rather than
silently changing meaning.
