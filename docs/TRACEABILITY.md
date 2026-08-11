# Requirement Traceability

## Implemented evidence

| Requirement | Implementation | Verification | Gate |
|---|---|---|---|
| AF-PLAT-001..002 | `global.json`, build controls, central package versions and lock files | Windows and WSL Ubuntu locked restore/build/test, format, host smoke | `M0-20260810.md` |
| AF-ARCH-001 | Domain, Abstractions, Setup, Host dependency direction | `DependencyDirectionTests` on Windows/Linux | `M0-20260810.md` |
| AF-SET-001 | installation state machine, file state reader, setup/status and guarded runtime endpoints | unit, security, integration, clean-launch E2E tests | `M0-20260810.md` |
| AF-HOST-001 | liveness/readiness checks and JSON health response | `HostReadinessTests`, Windows/Linux smoke | `M0-20260810.md` |
| AF-HOST-002 | default `127.0.0.1:5047` binding | configuration review and dual-platform smoke; remote hardening remains M10 | `M0-20260810.md` |
| AF-ENV-001 (passive profile foundation) | immutable environment records, bounded native metadata profiler, canonical SHA-256 fingerprint, redacted artifact and audit service, `environment inspect` CLI | Windows/Ubuntu/Kali fixture tests, live cross-platform capture, persistence/redaction/integrity test, CLI E2E | `M2-01-20260810.md` |
| AF-TOOL-001 (inventory foundation) | bounded PATH metadata inventory with provenance, symlink and conservative trust evidence; no candidate execution primitive | architecture source boundary, normalization/order tests, Windows and Ubuntu live capture; description/probing remains later M2 | `M2-01-20260810.md` |
| AF-DATA-001 (foundation) | EF Core SQLite migration, WAL initialization, installation repository, numeric concurrency, unit of work, outbox schema | `PersistenceFoundationTests`, repeat startup and Windows/Linux host smoke | `M1-01-20260810.md` |
| AF-AUD-001 (journal foundation) | sequenced append-only audit rows and SHA-256 hash chain | persistence survival, backup/restore, and chain assertions | `M1-01-20260810.md` |
| AF-AUD-001 (redaction/integrity) | `AgentForge.Audit` recorder, structured redaction boundary, length-prefixed event hashes, full-chain verifier | redaction persistence integration and exact-sequence tamper tests | `M1-02-20260810.md` |
| AF-SEC-003 (redaction portion) | bounded recursive JSON redactor for sensitive names and credential-shaped values | security tests for nested values, canonical output, size bounds, and raw-value absence | `M1-02-20260810.md` |
| AF-SET-002 (setup begin) | shared `ISetupApplicationService`, interactive/headless CLI input, atomic installation plus audit commit | unit conflict/validation tests, integration transaction test, CLI restart and equivalence E2E tests | `M1-03-20260810.md` |
| AF-SET-003..004 (provider portion) | deterministic provider validator, versioned profile repository, capability evidence, secret reference only | provider validation/persistence, baseline upgrade, cold restore, and plaintext-absence integration tests | `M1-04-20260810.md` |
| AF-SEC-003 (secret-store portion) | disposable leases, Windows DPAPI current-user store, Linux Secret Service adapter, deterministic fake | Windows encrypted-at-rest round trip, Linux typed-unavailable test, lease disposal, DB/audit byte scans | `M1-04-20260810.md` |
| AF-ID-001 | immutable agent identity plus separate routing, memory, capability, budget, child, and learning policies; versioned repository; CLI preview/create | pure policy unit tests, write-free preview and durable round-trip integration, headless CLI E2E on Windows/Linux | `M1-05-20260810.md` |
| AF-SEC-001/005 (bootstrap bounds) | conservative `Allow`/`Deny`/`RequireApproval` preview; exact-grant validation; locality, recursion, child-budget, credential, network, device, and promotion restrictions | fail-closed policy decisions, invalid-policy unit fixtures, migration and atomic create integration | `M1-05-20260810.md` |
| AF-SEC-001/005 (runtime policy foundation) | canonical authorization contexts; global missing/ambiguous-policy denial; exact agent-version rules; most-restrictive parent/child intersection; approval state machine | `CapabilityPolicyTests` cover canonical request identity, missing/ambiguous policy, exact active grant/denial, expiry/consumption, changed inputs, replay, and intersection | `M2-02-20260811.md` |
| AF-SEC-002 | authenticated preview/apply service and CLI; durable grants/denials bind installation/version, current agent/version, actors, capability/risk, tool/version, parameter/target/workspace hashes, expiry, correlation, request and preview hashes, and idempotency key | negative authentication/policy/lifetime tests, exact/conflicting replay, redaction/SQLite scan, migration upgrade, audit integrity, and Windows DPAPI CLI E2E | `M2-02-20260811.md` |
| AF-SET-003 | minimum-viability completion validates migration startup, audit integrity, materializable text-provider secret, named agent, and local administrator before the pure state-machine path reaches Ready | deterministic completion/restart, missing authority checks, Windows DPAPI CLI completion, Windows/Linux suites | `M1-06A-20260810.md` |
| AF-HOST-003 (authentication portion) | random OS-referenced administrator credential, verifier-only durable state, fixed-time authenticator, bearer-protected Ready runtime ping | credential security test, valid/invalid authentication integration, plaintext DB scan | `M1-06A-20260810.md` |
| AF-SET-004 | authenticated doctor/export service, versioned snapshot rows, content-addressed report and rollback JSON, automatic pre-recovery rollback capture | redaction/plaintext-absence, stale-version, migration-upgrade, restart, and Windows maintenance CLI tests | `M1-06B-20260810.md` |
| AF-SET-005 (transition portion) | credential-bound `Ready → RecoveryRequired → Configuring` transitions and authenticated recompletion with the existing administrator identity | wrong-credential rejection, state/version assertions, automatic snapshot, unhealthy recovery doctor, restart-safe recompletion | `M1-06B-20260810.md` |
| AF-SEC-003 (metadata/export portion) | credential-shape rejection before provider/agent/identifier persistence and redaction before report/profile artifacts | unit and integration rejection tests; exported artifact scan excludes verifier and materialized credential | `M1-06B-20260810.md` |
| AF-SET-002 (provider/maintenance CLI portion) | provider credentials enter only through bounded stdin/hidden prompt buffers; provider/agent preview and create/edit paths reuse setup application contracts | Windows live-DPAPI CLI setup-through-edit E2E; Ubuntu deterministic suite; CLI argument and plaintext-output assertions | `M1-06C-20260810.md` |
| AF-SET-005 (configuration-edit portion) | authenticated version-bound provider/agent previews and atomic hash-bound apply operations while `Configuring` | wrong-hash denial, stale-version conflict, no-write preview, persisted version increments, restart-safe recompletion | `M1-06C-20260810.md` |
| AF-SEC-003 (provider-onboarding portion) | invocation-scoped credential buffer, OS secret reference persistence, exact-reference compensation, no argument transport | compensation unit test, SQLite byte scan, live Windows CLI and deterministic Ubuntu verification | `M1-06C-20260810.md` |
| AF-SET-002 | interactive/headless entry plus provider, agent, completion, maintenance, restore, and shared validation services produce equivalent normalized Ready profiles | complete dual-profile Windows E2E and portable deterministic service suites | `M1-06D-20260810.md` |
| AF-SET-005 (rollback portion) | authenticated hash-bound rollback preview/apply in recovery configuration mode; topology-preserving provider/agent restore with current validation | valid restore/recompletion, wrong/no-op/tampered artifact denial, version assertions, audit and restart checks | `M1-06D-20260810.md` |
| AF-SET-004 (backup portion) | complete cold backup set retains SQLite, content-addressed artifacts, and OS-protected references with per-file hashes | Windows cold-copy hash equality, restored migration startup, healthy doctor, provider/agent equality | `M1-06D-20260810.md` |

Requirements marked `In progress` have partial gate evidence but remain open until
every acceptance criterion in the ledger is implemented and verified.

## R1 acceptance scenarios

| ID | Scenario | Primary requirements | Planned gate |
|---|---|---|---|
| AC-01 | Clean setup through provider validation and named agent | AF-SET-002..004, AF-ID-001 | M1 |
| AC-02 | Unified OpenAI, Anthropic, DeepSeek, compatible/vLLM contracts | AF-MODEL-001 | M3 |
| AC-03 | Correct Windows and Linux profiles | AF-ENV-001 | M2 |
| AC-04 | Safe Kali inventory and policy exposure | AF-TOOL-001..002 | M2 |
| AC-05 | Seed-skill proposal, canary, and rollback | AF-SKILL-001..002 | M5 |
| AC-06 | Restart-safe timezone schedule | AF-SCHED-001 | M4 |
| AC-07 | Cited Brave research surviving throttling | AF-SEARCH-001 | M7 |
| AC-08 | Remote local OpenAI-compatible endpoint | AF-MODEL-001..002 | M3 |
| AC-09 | Image task reroute or typed rejection | AF-MULTI-001 | M3 |
| AC-10 | Authenticated channel event, identity binding, dedupe | AF-CHAN-001 | M7 |
| AC-11 | Approved outbound message with delivery audit | AF-CHAN-001, AF-AUD-001 | M7 |
| AC-12 | Passive device discovery and explicit capture grant | AF-DEV-001 | M8 |
| AC-13 | Bounded capture and exact overflow reporting | AF-DEV-002 | M8 |
| AC-14 | Evidence-backed decoder proposal and promotion | AF-DEV-003 | M8 |
| AC-15 | Scheduled measurement and fake-channel alert provenance | AF-SCHED-001, AF-DEV-001..003, AF-CHAN-001 | M8 |
| AC-16 | Isolated non-trivial coding change | AF-CODE-001..002 | M6 |
| AC-17 | Interrupted coding task resume | AF-CODE-001, AF-TASK-001 | M6 |
| AC-18 | Corrected procedure becomes promoted revision | AF-LEARN-001..002 | M9 |
| AC-19 | Existing run retains its skill snapshot | AF-SKILL-001 | M5 |
| AC-20 | Faulty skill fails deterministic promotion | AF-SKILL-002, AF-LEARN-002 | M9 |
| AC-21 | Regressing canary rolls back | AF-SKILL-002 | M9 |
| AC-22 | Repeated chain produces verified bundle | AF-BUNDLE-001 | M9 |
| AC-23 | Cross-source prompt injection cannot expand authority | AF-SEC-001..005 | M2-M9 |
| AC-24 | Complete redacted trajectory export | AF-AUD-001..002 | M10 |
| AC-25 | Self-contained Windows/Linux installation smoke tests | AF-DEPLOY-001 | M10 |
