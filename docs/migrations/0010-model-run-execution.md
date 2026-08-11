# Migration 0010: Model run execution

Migration file: `20260811140344_ModelRunExecution.cs`

SHA-256: `894bd039e8cdce42dfddaa18257e97abfaa90ee434ca774364e5b99b54806fa8`

## Forward behavior

Adds persisted attempted-profile history, event reservation, optional start-lease owner/hash/times,
and terminal stream count/last-sequence/hash to `model_runs`; attempts gain the same stream evidence.
Creates `model_budget_ledgers`, foreign-key bound to one installation and agent, with exact agent
version, active input/output/tool/event/time reservations, active-run count, cumulative consumption,
update time, and numeric optimistic-concurrency version.

Legacy runs receive `[]` attempted profiles, a conservative two-event reservation, no lease, and
the canonical SHA-256 empty-stream evidence (`count=0`, `last=-1`). No run is fabricated or marked
started. New rows are written from pure state-machine records.

## Upgrade fixture

`Model_run_execution_migration_preserves_legacy_reserved_run_with_safe_defaults` migrates to 0009,
creates exact installation/provider/agent authority plus a legacy reserved run/attempt, applies the
latest migration, and proves the aggregate remains readable with the safe defaults and no invented
budget ledger.

## Verification

Execution integration tests cross DI scopes and prove atomic start/terminal audit plus ledger
reconciliation for success, malformed provider ordering, and caller cancellation. Exact replay does
not invoke the provider twice. SQLite byte scans verify representative prompt and output text are
absent. Unit tests cover lease tokens, event hashes/order/bounds, shared-budget overcommit,
agent-version pinning/roll-forward, terminal reconciliation, and duplicate release denial.

## Recovery and rollback

Before upgrading, stop AgentForge and copy SQLite including WAL/SHM members, artifacts, and OS secret
references as one hash-recorded backup. The generated down migration removes ledgers, leases,
attempted-profile history, event reservations, and stream evidence. Restore the complete pre-0010
backup instead of applying down to operator state. Never delete a `Running` row or zero a ledger to
force retry; preserve it for the later expired-lease recovery flow.
