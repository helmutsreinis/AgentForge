# Migration 0002: Provider profiles

Migration file: `20260810171602_ProviderProfiles.cs`

SHA-256: `7158e044bc16609063d0df4b649ca7a8bad42196ab58ef3c2367d6a422ba1872`

## Forward behavior

Creates the tenant-scoped `provider_profiles` table with an installation foreign key,
unique installation/name identity, numeric concurrency version, capability evidence,
provenance, and a secret store/key reference. There is deliberately no plaintext
credential column.

## Upgrade fixture

`Provider_profile_migration_upgrades_baseline_without_losing_installation` migrates an
empty database only to migration 0001, inserts installation state, applies migration
0002, and proves the installation survives and provider queries work.

## Backup and restore verification

The provider persistence integration creates a validated profile, makes a cold copy,
opens and migrates the restored copy, and verifies the exact secret reference and
profile identity. The plaintext test credential is also searched as UTF-8 bytes in
the database and must be absent.

## Recovery and rollback

Before applying migration 0002 to a populated installation, stop AgentForge and back
up the database, artifacts, and OS secret references as one recovery set. The generated
down migration drops provider metadata and is destructive; do not run it on operator
state. Restore the complete pre-migration backup instead.
