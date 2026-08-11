# Migration 0011: Model run recovery health

Migration file: `20260811191829_ModelRunRecoveryHealth.cs`

SHA-256: `446a90b5b8eb4aa82afdfae9a9898b84a1fa9c92357330e7eaf7ec6539a9bd90`

## Forward behavior

Creates `model_provider_health`, keyed by exact provider profile and foreign-key bound to its
installation, last model run, and last attempt. The row stores only typed status/source, bounded
failure count and evidence code, observed/expiry/retry timestamps, actor/correlation provenance,
and a numeric optimistic-concurrency version. It contains no provider response, endpoint, secret,
request content, output content, or raw lease token.

Existing providers and runs receive no fabricated health row. Missing evidence therefore continues
to fail closed in route planning until a bounded probe, operator override, or observed attempt
provides current evidence.

## Upgrade fixture

`Model_run_execution_migration_preserves_legacy_reserved_run_with_safe_defaults` starts on schema
0009 with an exact legacy reserved run/attempt, applies all current migrations, and proves the run
remains readable while both budget-ledger and provider-health rows remain absent. The default DI
fixture also proves `IModelProviderHealthSource` resolves to the scoped durable repository.

## Verification

Pure tests cover success reset, retryable failure count/backoff, lease-expiry classification,
authority mismatch, bounded metadata, monotonic heartbeat ownership/token/expiry, and deterministic
expired-lease completion. SQLite tests cover healthy and unavailable writes, cancellation
non-observation, hash-only heartbeat persistence, atomic recovery/ledger release/audit, duplicate
recovery, raw-token absence, and prior-schema upgrade.

## Recovery and rollback

Before upgrade, stop AgentForge and copy SQLite including WAL/SHM members, artifacts, and OS secret
references as one hash-recorded backup. The generated down migration destroys provider-health and
circuit-breaker provenance. Restore the complete pre-0011 backup instead of applying down to
operator state. Never delete or edit a running lease, health row, or ledger to force routing.
