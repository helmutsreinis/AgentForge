# ADR 0039: Scoped durable memory

Status: Accepted — 2026-08-12

## Decision

Memory is represented by seven explicit kinds with kind-specific provenance: working, task, episodic,
semantic, user, environment, and procedural. Entries are bounded, redacted before persistence, immutable,
expiring, source/content hashed, and bound to installation, agent, and scope. Full-text retrieval applies
that complete intersection and treats query wildcard characters literally.

Deletion requires the exact scope tuple, removes the relational entry, and appends hash-only audit in the
same unit of work. SQLite `secure_delete` is enabled. Memory content never participates in authorization.

## Consequences

Cross-task or cross-agent recall fails closed. Semantic facts require citation evidence, episodes require
trajectory evidence, environment facts require profile evidence, and procedures require skill or correction
evidence. Historical backups remain governed by their own retention/destruction lifecycle.
