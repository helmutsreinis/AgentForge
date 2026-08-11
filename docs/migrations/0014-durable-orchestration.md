# Migration 0014: Durable orchestration snapshots

Migration file: `20260811210954_DurableOrchestration.cs`

Migration SHA-256: `21a93cea0694dbf6db05f99efc51f45985e3458aabfb69ffd816325000564101`

## Forward behavior

Creates append-only `orchestration_task_snapshots` keyed by task and version. Each row binds the
installation and agent foreign keys, state, previous/current snapshot hashes, bounded authority and
correlation metadata, timestamps, and the complete typed snapshot JSON. A unique installation,
idempotency-key, and version index makes concurrent initial/transition appends collide safely.

Existing records are unchanged and no task or lease is fabricated for prior installations.

## Upgrade fixture

The migration drift check verifies the generated model exactly. The durable task integration
fixture initializes the current schema, seeds exact authority, appends versions 0-6 across fresh
scopes, recovers an expired lease, and proves the canonical chain and token absence.

## Recovery and rollback

Stop AgentForge and hash-copy SQLite with WAL/SHM files, artifacts, and secret references before
upgrade. The down migration removes all durable task/checkpoint/lease history; restore the complete
pre-0014 backup instead of applying down to operator data. Never edit task JSON or hashes to force
resume.
