# Requirement Traceability

## Implemented evidence

| Requirement | Implementation | Verification | Gate |
|---|---|---|---|
| AF-PLAT-001..002 | `global.json`, build controls, central package versions and lock files | Windows and WSL Ubuntu locked restore/build/test, format, host smoke | `M0-20260810.md` |
| AF-ARCH-001 | Domain, Abstractions, Setup, Host dependency direction | `DependencyDirectionTests` on Windows/Linux | `M0-20260810.md` |
| AF-SET-001 | installation state machine, file state reader, setup/status and guarded runtime endpoints | unit, security, integration, clean-launch E2E tests | `M0-20260810.md` |
| AF-HOST-001 | liveness/readiness checks and JSON health response | `HostReadinessTests`, Windows/Linux smoke | `M0-20260810.md` |
| AF-HOST-002 | default `127.0.0.1:5047` binding | configuration review and dual-platform smoke; remote hardening remains M10 | `M0-20260810.md` |
| AF-DATA-001 (foundation) | EF Core SQLite migration, WAL initialization, installation repository, numeric concurrency, unit of work, outbox schema | `PersistenceFoundationTests`, repeat startup and Windows/Linux host smoke | `M1-01-20260810.md` |
| AF-AUD-001 (journal foundation) | sequenced append-only audit rows and SHA-256 hash chain | persistence survival, backup/restore, and chain assertions | `M1-01-20260810.md` |
| AF-AUD-001 (redaction/integrity) | `AgentForge.Audit` recorder, structured redaction boundary, length-prefixed event hashes, full-chain verifier | redaction persistence integration and exact-sequence tamper tests | `M1-02-20260810.md` |
| AF-SEC-003 (redaction portion) | bounded recursive JSON redactor for sensitive names and credential-shaped values | security tests for nested values, canonical output, size bounds, and raw-value absence | `M1-02-20260810.md` |
| AF-SET-002 (setup begin) | shared `ISetupApplicationService`, interactive/headless CLI input, atomic installation plus audit commit | unit conflict/validation tests, integration transaction test, CLI restart and equivalence E2E tests | `M1-03-20260810.md` |
| AF-SET-003..004 (provider portion) | deterministic provider validator, versioned profile repository, capability evidence, secret reference only | provider validation/persistence, baseline upgrade, cold restore, and plaintext-absence integration tests | `M1-04-20260810.md` |
| AF-SEC-003 (secret-store portion) | disposable leases, Windows DPAPI current-user store, Linux Secret Service adapter, deterministic fake | Windows encrypted-at-rest round trip, Linux typed-unavailable test, lease disposal, DB/audit byte scans | `M1-04-20260810.md` |

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
