# Migration 0004: Local administrator

Migration file: `20260810174842_LocalAdministrator.cs`

SHA-256: `b129ed3bc45bd5460b1ff3c6c7fddec72fa769080f24a6cce25991c9c2f65d21`

## Forward behavior

Creates exactly one installation-bound `local_administrators` row with unique actor,
OS secret store/key reference, PBKDF2 algorithm/work factor/salt/verifier, concurrency
version, timestamps, and correlation provenance. No plaintext credential column
exists.

## Upgrade fixture

`Administrator_migration_preserves_existing_agent_configuration` migrates to 0003,
inserts installation/provider/agent state, applies 0004, and proves the exact agent
survives while no administrator is fabricated during migration.

## Verification

Completion creates the administrator and Ready state in one relational commit after
minimum-viability checks. Security tests prove correct/wrong verification behavior;
integration scans SQLite for exact materialized credential bytes. Windows E2E runs
the CLI through live current-user DPAPI.

## Recovery and rollback

Back up SQLite and OS secret references together. The generated down migration drops
administrator authentication state and is destructive. Restore the complete pre-0004
backup and remain in setup/recovery mode instead of applying down to operator state.
