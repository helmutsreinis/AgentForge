# ADR 0045: Governed recursive learning and skill bundles

Status: Accepted

## Decision

Store learning signals as immutable redacted evidence and classify them through a pure deterministic function.
Require exact successful usage evidence or an expiring operator authorization for an existing-skill revision.
Candidate generation occurs outside the active registry in a content-addressed proposal workspace, after which the
immutable agent-proposed package enters the same skill governance path as seed and user packages.

Require five distinct actors for worker, proposer, deterministic verifier, critic, and governor duties. Deterministic
target, holdout, adversarial, baseline, and permission evidence is a hard veto. Canary regression quarantines and an
exact promoted baseline may be restored only through the governed rollback transition.

Represent repeated skill chains as immutable bundle DAGs containing exact skill ID, semantic version, package hash,
and compatible input/output contract hashes. Copy no skill body. Derive permissions as the exact union of pinned
package authority. Persist proposal, verification, critique, activation, and archive separately, and revalidate all
pins immediately before activation.

## Consequences

Learning cannot approve itself, acquire authority from prompt text, rewrite an active package, or conceal a failed
evaluation. Corrections without usage or explicit operator authority remain memory. New packages and bundles become
usable only after their independent governance gates. Artifact and database backups must remain one recovery unit.
