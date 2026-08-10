# ADR 0011: Setup export and recovery authority

Status: Accepted

## Context

Local maintenance must diagnose and repair configuration without disclosing secrets,
silently overwriting concurrent changes, replacing the administrator, or allowing
normal agent work while configuration is unsafe.

## Decision

Doctor is read-only and returns bounded typed checks. Export and recovery mutations
require the exact durable installation version, the existing local administrator
actor, and a credential materialized from its OS reference for one invocation.

Reports and rollback profiles are structured, redacted, and stored in the existing
content-addressed artifact store. SQLite retains immutable snapshot metadata and
hashes. A rollback profile includes provider and client secret references but excludes
secret material and the administrator verifier. Entering recovery creates a fresh
pre-transition rollback profile in the same relational unit of work as the state
transition and audit event.

`RecoveryRequired` fails readiness. An authenticated resume returns to `Configuring`,
and minimum-viability completion must pass again before `Ready`. Recompletion reuses
the administrator identity/reference and authenticates it first.

## Consequences

Concurrent maintenance receives a typed conflict instead of last-write-wins behavior.
An artifact file can be orphaned if the relational commit fails, but it is immutable,
contains redacted content, and is not discoverable as an accepted snapshot. Snapshot
restore and configuration edits remain unavailable until their diff, version, and
validation gates pass. Losing the OS credential reference requires a separate future
break-glass design; it is not bypassed by recovery mode.
