# ADR 0024: Durable, Idempotent Model Run Admission

## Status

Accepted on 2026-08-11.

## Context

A short-lived route plan proves a bounded routing decision but is neither durable authority nor
an execution receipt. Calling a provider directly from a plan would lose the exact route and
budget evidence on interruption, allow duplicate submission after an uncertain retry, and leave
no atomic audit record. Persisting prompt content to solve replay would create a new credential
and privacy sink.

## Decision

Add pure model run and first-attempt state machines. Admission snapshots exact installation,
agent, provider, request, selection, health, prepared-input, context-policy, and plan hashes;
reserves input/output tokens, tool calls, and wall-clock time; and creates `Reserved`/`Planned`
records. Start, completion, failure, cancellation, and budget exhaustion are explicit terminal
transitions with numeric optimistic-concurrency versions and bounded usage/cost evidence.

Add a scoped `IModelRunAdmissionService`. It prepares context to calculate a redacted effective
request identity, binds that identity plus actor, correlation, authority versions, attempt
history, estimates, and the installation-scoped idempotency key into an admission hash, then
requests a fresh route plan. The plan must match the exact requested identities, versions, and
reservations and still be within its five-second lifetime.

The run, first attempt, and redacted `model.run-reserved` audit event commit in one EF unit of
work. Exact idempotent retries return the existing aggregate. Conflicting reuse fails with
`ConcurrencyConflict`; concurrent exact submissions converge through the unique database key.
SQLite stores hashes, typed evidence, reservations, usage, and provenance only. It has no prompt,
message, attachment bytes, endpoint, secret reference, credential, or provider response column.

Production provider and health catalogs remain empty. Admission reserves state only; it does not
resolve an adapter, materialize a credential, perform model egress, or expose a public API/CLI
mutation.

## Consequences

- A crash after admission leaves an auditable, idempotent `Reserved` run rather than an
  untraceable possible provider call.
- Token/tool/time limits are immutable reservation evidence, and overage cannot be recorded as
  ordinary success.
- Redaction-equivalent input can be retried without storing reconstructable model context.
- Migration 0009 adds two authority-bound tables and must be protected by the full cold-backup
  procedure; its generated down migration destroys run evidence.
- Cumulative agent accounting, reservation release/reconciliation, start leases, exact adapter
  resolution, destination/DNS checks, streaming checkpoints, retries, and no-progress loop logic
  remain later Milestone 3 gates.
