# Migration 0012: Model run retry and failover

Migration file: `20260811195031_ModelRunRetryFailover.cs`

Migration SHA-256: `6b2cc2a7d0d9160eb82d4b35aa125932effab14a976afc6ff4bf42cc2c8d7c22`

## Forward behavior

Adds maximum-attempt and cumulative wall-time fields to `model_runs`. Adds exact input/output/tool/
event/wall reservations to every `model_run_attempts` row. Existing runs receive `MaximumAttempts=1`
and zero cumulative wall time; existing attempt reservations are backfilled from their parent run.
No new attempt, route, usage, health, lease, or terminal state is fabricated.

## Upgrade fixture

`Model_run_execution_migration_preserves_legacy_reserved_run_with_safe_defaults` starts on schema
0009 with a real reserved run and attempt, applies all current migrations, and proves maximum one,
zero consumed wall time, and an attempt reservation exactly equal to the preserved run reservation.

## Verification

State-machine tests cover total reservation multiplication, exact ordered attempt history, distinct
fallback routes, sequence bounds, cumulative usage/cost/event/wall evidence, and exhausted/substituted
history denial. SQLite tests prove primary retryable failure, health write, exact exclusion, fallback
success, two immutable attempt rows, cumulative run evidence, per-attempt ledger reconciliation,
fallback-policy denial, total-agent-budget denial, idempotency binding, and audit ordering.

## Recovery and rollback

Before upgrading, stop AgentForge and copy SQLite including WAL/SHM members, artifacts, and OS secret
references as one hash-recorded backup. The generated down migration removes attempt reservations,
attempt limits, and cumulative wall evidence. Restore the complete pre-0012 backup instead of
applying down to operator state. Never delete a failed attempt or edit attempted-profile history to
force another provider call.
