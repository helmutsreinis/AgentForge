# Migration 0003: Agent identities

Migration file: `20260810173146_AgentIdentities.cs`

SHA-256: `97f6cf7f3264ddca75dc9aec45cfb1583c12dee3573eebed0b2d1f63d123a436`

## Forward behavior

Creates `agent_identities` with installation and primary-provider foreign keys,
case-insensitive unique installation/name identity, numeric concurrency version,
provenance, model-locality policy, memory/network scope, exact grant arrays, run and
child budgets, and learning/mutable-skill policy. Credentials and provider endpoint
data are not duplicated into agent rows.

## Upgrade fixture

`Agent_migration_upgrades_provider_schema_without_losing_profiles` migrates only to
0002, inserts installation and provider state, applies 0003, and proves the provider
survives while agent queries become available.

## Persistence verification

`Agent_preview_is_write_free_and_creation_persists_exact_effective_bounds` proves
preview writes neither agent nor audit state, then creates an agent and round-trips
its exact model, budget, child, memory, capability, and learning policy from a new
scope. The headless CLI acceptance test independently previews, creates, reopens, and
loads the identity.

## Recovery and rollback

Stop AgentForge and back up SQLite, artifacts, and OS secret references together
before applying 0003. The generated down migration drops all agent identities and is
destructive. Do not run it on operator state; restore the complete pre-0003 backup
and start in setup/recovery mode instead.
