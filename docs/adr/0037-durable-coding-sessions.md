# ADR 0037: Durable coding sessions

Status: Accepted

## Context

Build and test commands may be slow or interrupted. Re-running already completed commands can repeat
side effects, while storing objectives, source, command output, or patches directly in relational
state expands disclosure and recovery risk.

## Decision

Represent a coding session as an append-only canonical snapshot chain. Bind exact workspace,
authority, repository profile, backend, instruction hashes, typed plan, verification plan, actor,
idempotency, and correlation at creation. Store the objective as SHA-256 and the raw canonical patch
as a content-addressed artifact. Persist patch evidence, each completed verifier command, terminal
verification, Git diff review, failures, and completion as distinct versions with atomic audit.

Resume is state-driven and bounded. It applies an unapplied proposal, runs only verification commands
after the persisted result count, reviews only passing verification, and completes only a fully
evidenced plan. A prepared session still requires the caller to re-present the exact objective.

## Consequences

Process loss cannot repeat a recorded build/test command, terminal replay performs no work, and raw
objectives/source/output stay outside relational rows. Artifact backup is required to recover patch
content. An unrecorded command after an uncertain crash may be retried, so externally mutating
publish remains a separate exact-approval boundary.
