# Project State

Updated: 2026-08-10

## Current objective

Milestone 1 slice 6c: add version-bound provider/agent edit previews and apply paths,
finish the complete CLI setup journey, and close the milestone with backup/restore
and interactive/headless profile-equivalence evidence.

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
- M1 slice 6a added a random 256-bit local-administrator credential, OS-backed client reference, PBKDF2-SHA256 verifier-only server state, fixed-time authentication, minimum-viability checks, and the sole guarded `Configuring → Validating → Ready` completion path.
- Completion verifies audit integrity, a materializable text-provider secret, a named agent, and current storage; identity/state/audit commit atomically and the external credential is deleted on commit failure. Ready runtime ping requires the administrator bearer credential.
- Migration 0004 preserves existing agent configuration. Windows and Ubuntu builds, format, setup-only smoke, deterministic completion/authentication, and all 51 tests pass; Windows additionally passes live DPAPI CLI completion.
- M1 slice 6b added a bounded `doctor`, authenticated redacted setup report/export, content-addressed rollback profiles, version-bound recovery entry/resume, and reuse of the existing administrator identity during recovery recompletion.
- Recovery entry now writes a pre-transition rollback snapshot in the same relational transaction as the state change and redacted audit event. Exported profiles contain OS secret references but never administrator verifiers or materialized values.
- Migration 0005 preserves existing administrator state. Credential-shaped agent/provider metadata and actor/correlation identifiers fail before persistence. Windows and Ubuntu locked Release builds, format, host smoke, migration drift, dependency/secret scans, and all 55 tests pass; Windows additionally exercises the complete live DPAPI maintenance CLI path.

## Latest gate

`artifacts/gates/M1-06B-20260810.md`: Pass.

## Known constraints and risks

- Docker is not installed locally; container sandbox and image tests require an equipped CI runner until resolved.
- Local bearer authentication exists for the runtime ping, but request idempotency, rate limiting, authenticated mutations, session handling, and remote-mode controls remain later gates.
- Windows secret storage is available through current-user DPAPI. Linux requires a working Secret Service session and `secret-tool`; absence is a typed unsupported capability and never falls back to plaintext.
- Provider validation is deterministic only. Live provider adapters and model-context redaction remain disabled until Milestone 3.
- Setup may enter `Ready` only through minimum-viability completion. Linux live completion requires Secret Service; deterministic completion remains portable and live absence never degrades.
- Recovery entry and resume are authenticated and snapshot-backed, but profile edit/diff and automated snapshot restore are not enabled yet. Recovery remains configuration-only and cannot launch autonomous work.
- SQLite leases, inbox behavior, backup orchestration, and PostgreSQL parity remain later slices.

## Exact next action

Implement version-bound provider/agent edit preview and apply operations, expose the
remaining provider setup flow through the CLI, then close M1 only after cold
backup/restore and complete interactive/headless profile equivalence pass.
