# Migration 0013: Agent loop snapshots

Migration file: `20260811201331_AgentLoopSnapshots.cs`

Migration SHA-256: `ed461405f1a7d09e596cd4cc2bad7380874e87807c4e39f1614c17beb6f8fc81`

## Forward behavior

Creates append-only `agent_loop_snapshots` keyed by loop and sequence. Each row contains exact
installation/agent authority, phase and state, total budget/consumption, repair and no-progress
counters, correlation, normalized evidence hashes, and a previous/current snapshot hash pair.
Foreign keys restrict deletion of referenced installations and agents. A unique installation,
idempotency-key, and sequence index prevents two initial rows for one mutation identity.

Existing installation, provider, agent, model-run, ledger, and audit records are unchanged. The
migration creates no loop or snapshot evidence for prior state.

## Upgrade fixture

`Agent_loop_snapshot_migration_preserves_prior_authority_without_fabricating_runs` creates real
installation/provider/agent authority on migration 0012, applies all current migrations, proves the
authority remains readable, proves no loop is fabricated, and verifies migration 0013 is applied.

## Recovery and rollback

Before upgrade, stop AgentForge and copy SQLite including WAL/SHM members, artifacts, and OS secret
references as one hash-recorded backup. The generated down migration deletes every durable phase,
budget, completion, and recovery checkpoint. Restore the complete pre-0013 backup instead of
applying down to operator state. Never delete, resequence, rehash, or edit a snapshot to force resume.
