# ADR 0035: Governed skill packages

Status: Accepted

## Context

Portable instructions are model input and can also request tool authority. Mutable files, implicit
dependencies, session-time active lookups, or proposer-controlled promotion would permit authority
expansion and non-reproducible runs.

## Decision

Validate a root `SKILL.md` and exact versioned `skill.harness.json` as a bounded, strict UTF-8,
link-free package. Hash a deterministic path/length/content bundle and store it as one immutable
artifact. Relational records retain only descriptors, provenance, status, and hashes.

Install seed, user, and agent candidates through the same registry service. Promotion uses an
append-only proposal chain with deterministic target/holdout/adversarial evidence, separate actor
approval, permission diff, exact active-baseline hash, canary, quarantine, and rollback. A unique
active pointer changes atomically with status, proposal, and audit records.

Materialize an exact transitive version snapshot per run. Search exposes descriptors; package
Markdown opens only through that snapshot and is hash-verified again.

## Consequences

Runs remain reproducible across promotion and rollback. Missing policy, trust, dependencies, or
fresh baseline denies. The package format is intentionally narrower than arbitrary executable
plugins; compiled extensions remain the Milestone 10 out-of-process plugin gate.
