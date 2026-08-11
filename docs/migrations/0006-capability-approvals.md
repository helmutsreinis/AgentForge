# Migration 0006: Capability approvals

Migration file: `20260811045227_CapabilityApprovals.cs`

SHA-256: `aa40ca4732e2d82911d5233913cf409fb43094248caca69b6ed2e9333c528cc8`

## Forward behavior

Creates `capability_approvals` with installation and agent foreign keys, numeric UTC
ticks for deterministic SQLite ordering, numeric optimistic-concurrency version, exact
authorization hashes and identifiers, disposition/state, expiry, approver/correlation,
preview hash, and an installation-scoped unique idempotency key. No raw parameters,
target, workspace, administrator credential, or secret value column exists.

The latest-request lookup is indexed by installation, agent, request hash, and creation
ticks. Deletion of referenced installations or agents is restricted so durable authority
evidence cannot disappear through cascade behavior.

## Upgrade fixture

`Capability_approval_migration_preserves_existing_agent_profiles` migrates to the prior
setup-profile-snapshot schema, inserts installation/provider/agent state, applies the
latest migrations, and proves the exact agent survives while no approval is fabricated.

## Verification

Integration tests authenticate preview/apply, reject unknown policy, wrong credentials,
excessive lifetime, wrong preview hashes, and conflicting idempotency reuse; then prove
grant/denial durability, latest lookup, redacted audit-chain integrity, and raw sensitive
parameter absence from SQLite. Windows E2E uses the live current-user DPAPI administrator
reference through CLI preview, apply, and exact replay.

## Recovery and rollback

Before upgrading, stop AgentForge and copy SQLite, WAL/SHM members, artifacts, and OS
secret references as one hash-recorded backup. The generated down migration drops all
approval and denial evidence and is therefore destructive. Restore the complete pre-0006
backup and remain in setup/recovery mode instead of applying down to operator state.
