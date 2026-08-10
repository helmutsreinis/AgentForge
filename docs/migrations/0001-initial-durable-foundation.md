# Migration 0001: Initial durable foundation

Migration file: `20260810163716_InitialDurableFoundation.cs`

SHA-256: `1645d10d2664b6313a1a27093cbdeef329ae2e929f0ac140096db9d6d35f781b`

## Forward behavior

Creates installation, audit-event, artifact-metadata, and outbox tables, their unique
keys and indexes, and EF migration history. Startup then enables WAL, foreign keys,
and a bounded busy timeout for every newly opened store.

## Upgrade fixture

This is the baseline schema, so its upgrade fixture is an empty temporary directory.
The Windows and Ubuntu host smoke tests both migrate that fixture and then prove the
setup endpoint is reachable while normal runtime remains closed.

## Backup and restore verification

`Cold_backup_restores_installation_and_audit_chain` seeds installation and audit
state, closes all database scopes, makes a cold copy, migrates the restored copy, and
verifies the exact installation snapshot and audit-chain genesis link. Production
operators must back up the database and artifact directory together as described in
the runbook.

## Recovery and rollback

There is no destructive down migration because this is the first schema. If initial
migration fails, preserve the directory for diagnostics and restore the entire
pre-migration backup set. An empty, never-ready setup directory may be moved aside
only by the operator after its absolute path is verified.
