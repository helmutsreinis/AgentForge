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
| AF-ARCH-002 | The bootstrap kernel contains only configuration, recovery, policy, audit, loading, snapshots, and promotion primitives. | 0-5 | Verified |
| AF-SET-001 | A clean launch detects an uninitialized installation and exposes setup while normal runtime operations fail closed. | 0 | Verified |
| AF-SET-002 | Interactive and non-interactive setup use the same application services and validation rules. | 1 | Verified |
| AF-SET-003 | Setup validates storage, one text provider/model, policy, audit, and one named agent before Ready. | 1 | Verified |
| AF-SET-004 | Setup persists a versioned profile containing secret references only and produces a redacted report and rollback snapshot. | 1 | Verified |
| AF-SET-005 | Recovery mode repairs provider/plugin/skill configuration without launching autonomous work. | 1 | Verified |
| AF-SET-006 | Loopback web setup hides bootstrap security controls, resumes the active browser session, discovers a bounded model catalog with an explicit model-ID fallback, verifies the selected model, and supports credential-free private compatible endpoints without weakening CSRF, origin, idempotency, or completion gates. | Post-R1 | Verified |
| AF-HOST-001 | Liveness remains available during setup or recovery; readiness is healthy only in Ready state. | 0 | Verified |
| AF-HOST-002 | The default control plane is loopback-only; remote binding requires explicit HTTPS, exact origin, bounded temporary access code, authenticated session, and network-policy configuration. | 0,10 | Verified |
| AF-HOST-003 | REST mutations are correlated, authenticated, auditable, versioned, and idempotent. Streaming run events are authenticated. | 1,10 | Verified |
| AF-ADMIN-001 | A Ready installation exposes a protected single-operator workspace on loopback or explicitly enabled HTTPS remote origin that lists persisted agents, streams an explicit bounded prompt against an agent's pinned loopback/private model with active operator cancellation and durable terminal evidence, lists/cancels durable runs, and installs/lists validated seed skills without disclosing the administrator credential or bypassing tool and promotion gates. | Post-R1 | Verified |
| AF-ADMIN-002 | A Ready operator can edit bounded agent identity/instruction fields and the model ID on its existing pinned provider through an authenticated, versioned, hash-bound preview/apply flow. Model edits preserve endpoint and credential topology, require endpoint discovery and a live bounded probe, and every committed edit remains Ready, atomic, idempotent, concurrency-safe, and audited without changing capability authority. | Post-R1 | Verified |
| AF-ADMIN-003 | A Ready operator can version and audit an agent's generated-output ceiling without changing any other budget or capability authority, and can select scalable response presets or an exact per-run output-token limit no greater than that durable ceiling or the server hard cap. | Post-R1 | Verified |
| AF-ADMIN-004 | A Ready operator can activate an exact installed skill through deterministic evaluation, separate approval, canary, and rollback gates, then preview and apply one exact per-agent skill grant. Active status and agent grant are independently required for run selection; skill authority never implies tool, network, file, message, device, or credential authority. | Post-R1 | Verified |
| AF-ADMIN-005 | A Ready operator can inspect immutable callable-tool descriptors, preview/apply one exact per-agent tool-capability grant with a bounded invocation ceiling, and approve or deny one exact expiring invocation. Execution requires both the current grant and a single-use approval bound to tool/version/descriptor, canonical parameters, target, workspace, installation/agent versions, and request hash; managed workspace reads reject traversal, links, binary content, unbounded output, processes, environment access, and network access. | Post-R1 | Verified |
| AF-ADMIN-006 | A Ready operator can create a live-validated provider and a complete independently versioned agent, and can preview/apply the entire current agent policy (routing, locality/fallback, memory, exact grants, run/child budgets, and learning) through an authenticated, idempotent, hash-bound, atomic, and audited transaction. Provider credentials are reference-only and must be re-entered for apply; previews retain only a credential fingerprint. Authority-version changes preserve old run snapshots and require a new conversation. | Post-R1 | Verified |
| AF-ADMIN-007 | A Ready operator can preview/create immutable agent-run schedules, control pause/resume/run-now, curate and delete exact-scope memory, execute an exact approved provider-neutral research request, and explicitly attach bounded memory/citation evidence to a local run. Scheduled occurrences execute as restart-safe durable conversations against pinned agent/provider/policy/budget/skill authority; memory and research remain untrusted reference data and never grant tools, network, policy, or external side effects to the model. | Post-R1 | Verified |
| AF-ADMIN-008 | Agent model discovery records an endpoint-reported context ceiling with model provenance; a Ready operator may leave that ceiling automatic or version a lower override, plus compression threshold/target and protected-tail settings. Multi-turn runs reserve output space, expose active occupancy, retain the full immutable transcript, and deterministically compress only older active context when the configured threshold is reached. | Post-R1 | Verified |

## Durable state, identity, and security

| ID | Requirement and acceptance criterion | Milestone | Status |
|---|---|---:|---|
| AF-DATA-001 | SQLite is the default durable store with migrations, optimistic concurrency, leases, and transactional inbox/outbox records. | 1 | Verified |
| AF-DATA-002 | PostgreSQL implements the same repositories and passes migration, backup, and restore tests. | 10 | Verified |
| AF-AUD-001 | Significant actions append redacted, sequenced, correlated events with actor, versions, hashes, outcome, duration, usage, and approval evidence. | 1 | Verified |
| AF-AUD-002 | A redacted trajectory can reconstruct context, snapshots, calls, retries, errors, and verification without disclosing credentials. | 1-10 | Verified |
| AF-ID-001 | Agent identity is distinct from provider selection and includes scope, model policy, budgets, memory, grants, child limits, and learning mode. | 1 | Verified |
| AF-SEC-001 | Capability policy returns Allow, Deny, or RequireApproval outside model control; missing policy denies. | 2 | Verified |
| AF-SEC-002 | Sensitive approval binds exact actor, agent, target, parameters, workspace, tool version, expiration, and request hash. | 2 | Verified |
| AF-SEC-003 | Secrets use references, invocation-scoped materialization, and pre-persistence/model/export redaction. | 1 | Verified |
| AF-SEC-004 | Untrusted processes use argument arrays, containment, limits, cancellation, process-tree termination, and declared network/filesystem policy. | 2 | Verified |
| AF-SEC-005 | No agent or child can increase its own permissions, budgets, recursion, network, credentials, or mutable-skill scope. | 2,4 | Verified |

## Runtime, tools, and orchestration

| ID | Requirement and acceptance criterion | Milestone | Status |
|---|---|---:|---|
| AF-ENV-001 | A hashed environment profile describes OS, runtime, shells, packages, services, privilege, container/VM/WSL, filesystem, network, and accelerators. | 2 | Verified |
| AF-TOOL-001 | Discovery inventories Windows, Linux, and Kali tools without executing or authorizing them. | 2 | Verified |
| AF-TOOL-002 | Every callable tool has typed schemas, provenance, risk, permissions, timeout, output bound, side effects, and progressive discovery. | 2 | Verified |
| AF-MODEL-001 | Provider-neutral contracts support streaming, structured output, tools, usage, cancellation, fallback, and hosted/local providers. | 3 | Verified |
| AF-MODEL-002 | Routing uses the intersection of declared, observed, configured, and policy-approved capabilities and respects data locality. | 3 | Verified |
| AF-MULTI-001 | Media is routed only to an approved capable model/extractor or returns typed UnsupportedCapability; media is never silently dropped. | 3 | Verified |
| AF-RUN-001 | The typed agent loop persists immutable run snapshots, budgets, stop conditions, cancellation, loop detection, and typed results. | 3 | Verified |
| AF-RUN-002 | Operator runs retain bounded redacted multi-turn context as hash-verified artifacts, expose authenticated details, and resume only incomplete leased work without replaying completed model turns. | Post-R1 | Verified |
| AF-TASK-001 | Work is a durable DAG with checkpoints, leases, heartbeats, bounded retry, idempotency, compensation, interruption recovery, and evidence. | 4 | Verified |
| AF-TASK-002 | Delegation bounds depth/count/concurrency and gives children only intersected context, capability, and budget. | 4 | Verified |
| AF-SCHED-001 | Durable one-shot, interval, cron-like, and calendar schedules handle timezone/DST, misfire, overlap, idempotency, pause/resume, and preview. | 4 | Verified |

## Skills, coding, and learning

| ID | Requirement and acceptance criterion | Milestone | Status |
|---|---|---:|---|
| AF-SKILL-001 | Portable SKILL.md packages use a validated harness sidecar, immutable versions, content hashes, dependencies, and run snapshots. | 5 | Verified |
| AF-SKILL-002 | Seed and created skills share proposal, evaluation, promotion, canary, rollback, quarantine, archive, signature, and provenance behavior. | 5 | Verified |
| AF-CODE-001 | Coding work uses isolated Git worktrees, semantic navigation, hash-bound patches, build/test/review verification, and durable checkpoints. | 6 | Verified |
| AF-CODE-002 | Unrelated operator changes remain untouched and external coding backends cannot bypass AgentForge policy or verification. | 6 | Verified |
| AF-LEARN-001 | Evidence-backed learning separates worker, proposer, deterministic verifier, critic, and governor roles. | 9 | Verified |
| AF-LEARN-002 | Existing-skill revisions require usage authority and baseline hashes; deterministic failure always vetoes promotion. | 9 | Verified |
| AF-LEARN-003 | The Ready operator workspace binds a bounded redacted learning signal to an exact terminal run receipt, deterministically classifies and lists it, preserves exact idempotency and audit evidence, and grants no candidate, policy, tool, or promotion authority. | Post-R1 | Verified |
| AF-LEARN-004 | A Ready `NewSkill` signal creates at most one immutable AgentProposal package in an isolated content-addressed workspace; versioned verifier, critic, governor, canary, promotion, and rollback controls preserve five-role separation and never make failed or rolled-back candidates available to runs. | Post-R1 | Verified |
| AF-LEARN-005 | A Proposed learning candidate is evaluated only by the server-owned managed isolated evaluator: it reopens and hash-verifies the immutable workspace, rejects unsafe archive entries, validates the exact installed package twice, runs hostile authority-escalation and bounded permission-diff checks, stores a content-addressed deterministic JSON receipt, and advances or rejects without accepting pass flags, scores, evidence hashes, package content, or permissions from the browser. | Post-R1 | Verified |
| AF-LEARN-006 | A Ready `NewSkill` signal can generate its inert candidate body through the exact source run agent's current pinned credential-free local/private text provider only when local-only/no-fallback routing and proposal-workspace learning authority are current. Source/guidance is untrusted and preflight-redacted; output must be one strict bounded JSON Markdown document, pass sensitive-content rejection, and bind signal, candidate, agent/provider/model versions, request/response/body hashes, and model evidence into the immutable package before automated evaluation. | Post-R1 | Verified |
| AF-BUNDLE-001 | Repeated successful chains may produce decomposable skill DAGs with pinned contracts and baseline comparison. | 9 | Verified |

## Integrations, devices, and delivery

| ID | Requirement and acceptance criterion | Milestone | Status |
|---|---|---:|---|
| AF-SEARCH-001 | Search supports Brave, a configurable official Google path, fakes, normalization, dedupe, rank fusion, extraction, caching, and citations. | 7 | Verified |
| AF-MEM-001 | Working, task, episodic, semantic, user/environment, and procedural memory are separately scoped, bounded, attributable, and removable. | 7 | Verified |
| AF-CHAN-001 | Telegram and official WhatsApp Business adapters normalize authenticated, replay-protected inbound/outbound events with identity binding and deterministic fakes. | 7 | Verified |
| AF-DEV-001 | Serial/USB discovery is passive and separates inventory, capture, read, write, command, calibration, firmware, and privileged capabilities. | 8 | Verified |
| AF-DEV-002 | Captures are bounded immutable evidence with raw bytes, timing, hashes, truncation/drop accounting, and deterministic replay. | 8 | Verified |
| AF-DEV-003 | Decoder proposals preserve unknown fields and pass replay, malformed input, fuzz, holdout, canary, promotion, and rollback gates. | 8 | Verified |
| AF-MCP-001 | The harness supports MCP client/server stdio and Streamable HTTP with policy-filtered exposure. | 10 | Verified |
| AF-PLUGIN-001 | Strict content-addressed plugin packages use signature-derived trust; only verified low-risk adapters may load in-process and all other plugins require the constrained worker protocol. | 10 | Verified |
| AF-DEPLOY-001 | R1 ships self-contained Windows/Linux artifacts, container image, Windows service, systemd unit, checksums, SBOM, and migration/runbook documentation. | 10 | Verified |
| AF-QUAL-001 | R1 passes all 25 acceptance scenarios and has no unresolved high-severity security finding. | 10 | Verified |

## Definition of done

Each requirement needs working code, public behavior documentation, deterministic
tests, traceability, audit behavior where significant, failure/cancellation/recovery
coverage, security and portability review, and a passing gate. Future TODO text is
not completion evidence.
