# Migration 0016: Durable schedule snapshots

Migration file: `20260811213129_DurableSchedules.cs`

Migration SHA-256: `2bbf7eec3f79312bb07111b95d0bb512e30ffaafb3695d575fec20333fd11e28`

## Forward behavior

Creates append-only `schedule_snapshots` keyed by schedule and version. Rows bind installation and
agent foreign keys, state, next base/due instants, previous/current hashes, typed snapshot JSON,
actor/correlation metadata, and timestamps. Latest due scans use the state/next-due index. A unique
installation/idempotency/version index closes concurrent creation and transition races.

No schedule, occurrence, or run is fabricated for existing installations.

## Upgrade fixture

Migration drift verification proves the model is exact. The durable orchestration integration
fixture creates, previews, replays, discovers, dispatches, abandons, recovers, completes, and
run-now replays a one-shot schedule through versions 0-6 in fresh scopes, then verifies every hash.

## Recovery and rollback

Stop AgentForge and hash-copy SQLite/WAL/SHM, artifacts, and secret references. Down migration
destroys schedule/checkpoint/lease history; restore the complete pre-0016 backup instead. Never edit
next-due values or snapshot JSON to force a run.
