# AgentForge Requirements Ledger

Status values are `Planned`, `In progress`, `Verified`, or `Blocked`. A requirement
is `Verified` only when traceability names working code, deterministic tests, and a
passing gate report.

## Bootstrap and platform

| ID | Requirement and acceptance criterion | Milestone | Status |
|---|---|---:|---|
| AF-PLAT-001 | The same .NET host builds and starts on supported Windows and Linux environments without platform logic in Domain. | 0 | Verified |
| AF-PLAT-002 | Projects use locked dependencies, nullable annotations, analyzers, deterministic output, and warnings-as-errors. | 0 | Verified |
| AF-ARCH-001 | Domain has no infrastructure dependencies and feature implementations communicate through Abstractions. | 0 | Verified |
| AF-ARCH-002 | The bootstrap kernel contains only configuration, recovery, policy, audit, loading, snapshots, and promotion primitives. | 0-5 | In progress |
| AF-SET-001 | A clean launch detects an uninitialized installation and exposes setup while normal runtime operations fail closed. | 0 | Verified |
| AF-SET-002 | Interactive and non-interactive setup use the same application services and validation rules. | 1 | In progress |
| AF-SET-003 | Setup validates storage, one text provider/model, policy, audit, and one named agent before Ready. | 1 | In progress |
| AF-SET-004 | Setup persists a versioned profile containing secret references only and produces a redacted report and rollback snapshot. | 1 | In progress |
| AF-SET-005 | Recovery mode repairs provider/plugin/skill configuration without launching autonomous work. | 1 | Planned |
| AF-HOST-001 | Liveness remains available during setup or recovery; readiness is healthy only in Ready state. | 0 | Verified |
| AF-HOST-002 | The default control plane is loopback-only; remote binding requires explicit hardened configuration. | 0,10 | Verified for local default |
| AF-HOST-003 | REST mutations are correlated, authenticated, auditable, versioned, and idempotent. Streaming run events are authenticated. | 1,10 | Planned |

## Durable state, identity, and security

| ID | Requirement and acceptance criterion | Milestone | Status |
|---|---|---:|---|
| AF-DATA-001 | SQLite is the default durable store with migrations, optimistic concurrency, leases, and transactional inbox/outbox records. | 1 | In progress |
| AF-DATA-002 | PostgreSQL implements the same repositories and passes migration, backup, and restore tests. | 10 | Planned |
| AF-AUD-001 | Significant actions append redacted, sequenced, correlated events with actor, versions, hashes, outcome, duration, usage, and approval evidence. | 1 | In progress |
| AF-AUD-002 | A redacted trajectory can reconstruct context, snapshots, calls, retries, errors, and verification without disclosing credentials. | 1-10 | Planned |
| AF-ID-001 | Agent identity is distinct from provider selection and includes scope, model policy, budgets, memory, grants, child limits, and learning mode. | 1 | Planned |
| AF-SEC-001 | Capability policy returns Allow, Deny, or RequireApproval outside model control; missing policy denies. | 2 | Planned |
| AF-SEC-002 | Sensitive approval binds exact actor, agent, target, parameters, workspace, tool version, expiration, and request hash. | 2 | Planned |
| AF-SEC-003 | Secrets use references, invocation-scoped materialization, and pre-persistence/model/export redaction. | 1 | In progress |
| AF-SEC-004 | Untrusted processes use argument arrays, containment, limits, cancellation, process-tree termination, and declared network/filesystem policy. | 2 | Planned |
| AF-SEC-005 | No agent or child can increase its own permissions, budgets, recursion, network, credentials, or mutable-skill scope. | 2,4 | Planned |

## Runtime, tools, and orchestration

| ID | Requirement and acceptance criterion | Milestone | Status |
|---|---|---:|---|
| AF-ENV-001 | A hashed environment profile describes OS, runtime, shells, packages, services, privilege, container/VM/WSL, filesystem, network, and accelerators. | 2 | Planned |
| AF-TOOL-001 | Discovery inventories Windows, Linux, and Kali tools without executing or authorizing them. | 2 | Planned |
| AF-TOOL-002 | Every callable tool has typed schemas, provenance, risk, permissions, timeout, output bound, side effects, and progressive discovery. | 2 | Planned |
| AF-MODEL-001 | Provider-neutral contracts support streaming, structured output, tools, usage, cancellation, fallback, and hosted/local providers. | 3 | Planned |
| AF-MODEL-002 | Routing uses the intersection of declared, observed, configured, and policy-approved capabilities and respects data locality. | 3 | Planned |
| AF-MULTI-001 | Media is routed only to an approved capable model/extractor or returns typed UnsupportedCapability; media is never silently dropped. | 3 | Planned |
| AF-RUN-001 | The typed agent loop persists immutable run snapshots, budgets, stop conditions, cancellation, loop detection, and typed results. | 3 | Planned |
| AF-TASK-001 | Work is a durable DAG with checkpoints, leases, heartbeats, bounded retry, idempotency, compensation, interruption recovery, and evidence. | 4 | Planned |
| AF-TASK-002 | Delegation bounds depth/count/concurrency and gives children only intersected context, capability, and budget. | 4 | Planned |
| AF-SCHED-001 | Durable one-shot, interval, cron-like, and calendar schedules handle timezone/DST, misfire, overlap, idempotency, pause/resume, and preview. | 4 | Planned |

## Skills, coding, and learning

| ID | Requirement and acceptance criterion | Milestone | Status |
|---|---|---:|---|
| AF-SKILL-001 | Portable SKILL.md packages use a validated harness sidecar, immutable versions, content hashes, dependencies, and run snapshots. | 5 | Planned |
| AF-SKILL-002 | Seed and created skills share proposal, evaluation, promotion, canary, rollback, quarantine, archive, signature, and provenance behavior. | 5 | Planned |
| AF-CODE-001 | Coding work uses isolated Git worktrees, semantic navigation, hash-bound patches, build/test/review verification, and durable checkpoints. | 6 | Planned |
| AF-CODE-002 | Unrelated operator changes remain untouched and external coding backends cannot bypass AgentForge policy or verification. | 6 | Planned |
| AF-LEARN-001 | Evidence-backed learning separates worker, proposer, deterministic verifier, critic, and governor roles. | 9 | Planned |
| AF-LEARN-002 | Existing-skill revisions require usage authority and baseline hashes; deterministic failure always vetoes promotion. | 9 | Planned |
| AF-BUNDLE-001 | Repeated successful chains may produce decomposable skill DAGs with pinned contracts and baseline comparison. | 9 | Planned |

## Integrations, devices, and delivery

| ID | Requirement and acceptance criterion | Milestone | Status |
|---|---|---:|---|
| AF-SEARCH-001 | Search supports Brave, a configurable official Google path, fakes, normalization, dedupe, rank fusion, extraction, caching, and citations. | 7 | Planned |
| AF-MEM-001 | Working, task, episodic, semantic, user/environment, and procedural memory are separately scoped, bounded, attributable, and removable. | 7 | Planned |
| AF-CHAN-001 | Telegram and official WhatsApp Business adapters normalize authenticated, replay-protected inbound/outbound events with identity binding and deterministic fakes. | 7 | Planned |
| AF-DEV-001 | Serial/USB discovery is passive and separates inventory, capture, read, write, command, calibration, firmware, and privileged capabilities. | 8 | Planned |
| AF-DEV-002 | Captures are bounded immutable evidence with raw bytes, timing, hashes, truncation/drop accounting, and deterministic replay. | 8 | Planned |
| AF-DEV-003 | Decoder proposals preserve unknown fields and pass replay, malformed input, fuzz, holdout, canary, promotion, and rollback gates. | 8 | Planned |
| AF-MCP-001 | The harness supports MCP client/server stdio and Streamable HTTP with policy-filtered exposure. | 10 | Planned |
| AF-DEPLOY-001 | R1 ships self-contained Windows/Linux artifacts, container image, Windows service, systemd unit, checksums, SBOM, and migration/runbook documentation. | 10 | Planned |
| AF-QUAL-001 | R1 passes all 25 acceptance scenarios and has no unresolved high-severity security finding. | 10 | Planned |

## Definition of done

Each requirement needs working code, public behavior documentation, deterministic
tests, traceability, audit behavior where significant, failure/cancellation/recovery
coverage, security and portability review, and a passing gate. Future TODO text is
not completion evidence.
