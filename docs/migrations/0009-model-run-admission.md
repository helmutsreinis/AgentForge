# Migration 0009: Model run admission

Migration file: `20260811132212_ModelRunAdmission.cs`

SHA-256: `9d75e40fea4d19292f902b0853f9e46c31bbefa18d8b350558194d845ceef87c`

## Forward behavior

Creates `model_runs` and `model_run_attempts`. Runs are foreign-key bound to the exact
installation, agent, and provider profile; first attempts are bound to their run and provider.
The run has a unique installation/idempotency key and an installation/agent/creation index.
Attempts have a unique run/sequence key. Both records use numeric optimistic-concurrency
versions.

Columns retain only stable identities, authority versions, normalized capability JSON, route,
plan, prepared-input, health, and admission hashes, context-preparation evidence, typed state,
token/tool/time reservation and usage, optional normalized cost/currency, timestamps, actor,
correlation, and terminal classification. There is no prompt, message, attachment data, endpoint,
secret reference, credential, raw provider output, or remote error body column.

## Upgrade fixture

`Model_run_admission_migration_preserves_existing_agent_authority` migrates to the prior
policy-bound-tool-invocation schema, inserts installation/provider/agent authority, applies the
latest migration, and proves that authority survives while no model run is fabricated.

## Verification

Integration tests create a route-backed reservation, cross a new DI scope, reload the aggregate,
verify the single chained audit event, prove exact/conflicting idempotency, converge concurrent
exact submissions, reject failed planning and sensitive metadata without writes, and byte-scan
SQLite for raw prompt and credential fixtures. State-machine tests cover expiry, immutable
snapshots, cancellation, retry classification, and input/output/tool/wall-clock exhaustion.

## Recovery and rollback

Before upgrading, stop AgentForge and copy SQLite including WAL/SHM members, artifacts, and OS
secret references as one hash-recorded backup. The generated down migration drops both model-run
tables and all admission/attempt evidence. Restore the complete pre-0009 backup and remain in
setup/recovery mode instead of applying down to operator state.
