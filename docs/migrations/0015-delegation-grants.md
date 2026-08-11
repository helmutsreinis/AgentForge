# Migration 0015: Delegation grants

Migration file: `20260811212132_DelegationGrants.cs`

Migration SHA-256: `c0d3500050e5435f421616c1e77b642034b7076b30458efef58ca4874fa876b9`

## Forward behavior

Creates immutable `delegation_grants` keyed by delegation ID. Each grant binds installation,
parent task, parent and child agent foreign keys, canonical grant hash, bounded actor/correlation
metadata, issuance/expiry, and the complete typed authority JSON. It stores context evidence hashes
and capability IDs, never context bodies or credentials.

No child grant is fabricated for existing tasks or agents.

## Upgrade fixture

Migration drift verification proves the current model matches generated migrations. The durable
orchestration fixture creates and idempotently replays a grant through independent repository and
audit boundaries before completing restart recovery.

## Recovery and rollback

Stop AgentForge and hash-copy SQLite/WAL/SHM, artifacts, and secret references. Down migration loses
delegation provenance and must not be applied to live operator state; restore the pre-0015 backup.
