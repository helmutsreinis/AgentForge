# Project State

Updated: 2026-08-10

## Current objective

Milestone 1 slice 6: complete minimum-viability validation, local administrator
bootstrap, doctor/report/export, rollback snapshots, edit/diff, recovery, and the
guarded transition to `Ready`.

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
- M1 slice 2 added independent Security and Audit modules, bounded canonical JSON redaction, conservative credential-name/value detection, a redaction-only audit recorder, length-prefixed event hashing, and complete chain verification.
- Nested secret removal, payload bounds, canonical output, delimiter ambiguity, persisted-value absence, valid-chain, and exact-sequence tamper behavior pass in the 29-test Windows/Ubuntu suite.
- M1 slice 3 added a shared setup application service, stable identifier generation, atomic installation/audit persistence, typed retryable conflict handling, deterministic headless input, and an equivalent interactive CLI prompt flow.
- Restart persistence, duplicate transition rejection, control-character validation, interactive/headless equivalence, atomic state/audit integrity, and dual-platform behavior pass in the 34-test suite.
- M1 slice 4 added disposable secret leases, a Windows current-user DPAPI store, a bounded Linux Secret Service adapter, typed unavailability, a deterministic fake, versioned provider profiles, capability evidence, and deterministic provider validation.
- Plaintext absence from encrypted files, SQLite bytes, audit JSON, and profile objects; baseline migration; cold restore; endpoint credential rejection; typed uniqueness races; and provider state/audit atomicity pass in the 40-test suite.
- M1 slice 5 added immutable named-agent definitions, separate model-routing policy, memory and network posture, exact grants, bounded run/child budgets, learning policy, a conservative effective-capability evaluator, and durable agent profiles.
- Preview is write-free; create re-evaluates the same policy, commits identity plus redacted audit atomically, and is available through deterministic application-service and headless CLI paths. Unsafe locality fallback, child-budget escalation, mutable-skill mismatches, invalid grants, and unobserved provider capability fail typed.
- Migration 0003 preserves migration-0002 provider profiles. Windows and Ubuntu builds, format, setup-only host smoke, CLI preview/create, and all 47 tests pass.

## Latest gate

`artifacts/gates/M1-05-20260810.md`: Pass.

## Known constraints and risks

- Docker is not installed locally; container sandbox and image tests require an equipped CI runner until resolved.
- No authenticated administration exists yet. Therefore normal runtime operations remain disabled by design.
- Windows secret storage is available through current-user DPAPI. Linux requires a working Secret Service session and `secret-tool`; absence is a typed unsupported capability and never falls back to plaintext.
- Provider validation is deterministic only. Live provider adapters and model-context redaction remain disabled until Milestone 3.
- Setup can enter `Configuring`, persist a validated provider and named agent, and preview the exact conservative policy. It cannot create an administrator or enter `Ready`; those gates remain deliberately closed.
- SQLite leases, inbox behavior, backup orchestration, and PostgreSQL parity remain later slices.

## Exact next action

Start M1 slice 6 with the OS-backed local administrator credential and minimum-
viability validator. Then add doctor, redacted setup export/report and rollback
snapshot, edit/diff, recovery behavior, and the only authorized `Ready` transition.
