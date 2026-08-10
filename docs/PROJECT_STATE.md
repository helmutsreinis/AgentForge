# Project State

Updated: 2026-08-10

## Current objective

Milestone 1 slice 2: enforce structured redaction before audit persistence and add
audit-chain verification without enabling autonomous runtime operations.

## Completed

- Git repository and .NET 10 solution initialized.
- Central package management, lock files, analyzers, warnings-as-errors, and CI matrix configured.
- Domain/Abstractions/Setup/Host/CLI boundaries established and architecture-tested.
- Pure installation state machine and fail-closed file state reader implemented.
- Loopback host provides liveness, readiness, setup status, correlated status, and guarded runtime ping.
- Six required test suites have executable responsibilities.
- Microsoft Agent Framework 1.17 compatibility spike proves typed workflow output, streaming events, and cancellation-token propagation while remaining outside production modules.
- M0 passed on Windows and WSL Ubuntu; both platforms built and passed all 18 tests, and both host smokes returned live=200, setup=200, runtime=503.
- M1 slice 1 added EF Core SQLite migrations and WAL initialization, transactional installation state, typed optimistic-concurrency conflicts, an outbox schema, content-addressed artifacts, and a sequenced SHA-256 audit chain.
- Cold backup/restore, repeat startup, persistence, concurrency, artifact idempotency, and dual-platform host startup are verified in a 22-test suite.
- A newly published high-severity advisory against SQLitePCLRaw 2.1.11 was caught by the Linux restore; the stable 2.1.12 bundle is directly pinned and the full vulnerability scan is clean.

## Latest gate

`artifacts/gates/M1-01-20260810.md`: Pass.

## Known constraints and risks

- Docker is not installed locally; container sandbox and image tests require an equipped CI runner until resolved.
- No secret store or authenticated administration exists yet. Therefore normal runtime operations remain disabled by design.
- Audit records accept only the `RedactedData` boundary type, but the structured redactor and verification service are the next slice; no externally supplied audit payload may be connected before that gate.
- SQLite leases, inbox behavior, backup orchestration, and PostgreSQL parity remain later slices.

## Exact next action

Start M1 slice 2: add Security and Audit application modules, deterministic structured
redaction, sensitive-value detection, audit-chain verification, and tamper/redaction
security tests.
