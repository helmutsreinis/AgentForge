# Decisions Ledger

| ID | Decision | Status |
|---|---|---|
| D-001 | Deliver the complete Production R1 plan through Milestones 0-10. | Accepted |
| D-002 | Optimize R1 for a local single operator while retaining tenant/scope identifiers in contracts. | Accepted |
| D-003 | Deliver CLI/TUI setup before the loopback web wizard; both share application services. | Accepted |
| D-004 | Use `AgentForge.*` namespaces and `agentforge` as the CLI executable name. | Accepted |
| D-005 | Use a modular monolith and add projects only when a working vertical slice needs them. | Accepted |
| D-006 | Keep Microsoft Agent Framework optional and outside durable domain ownership. | Accepted after spike |
| D-007 | Treat unavailable accounts, credentials, containers, and hardware as named live gates; deterministic fakes remain mandatory. | Accepted |
| D-008 | Directly pin patched native transitive dependencies when an upstream framework release still resolves a vulnerable build; never suppress the advisory. | Accepted |
| D-009 | Use current-user DPAPI on Windows and Secret Service through `secret-tool` on Linux; unavailable facilities fail typed and never fall back to reversible/plaintext storage. | Accepted |
| D-010 | Keep agent identity independent from provider identity and default bootstrap policy to local-only, no external authority, bounded budgets, and `Propose` learning; preview is write-free and create re-evaluates. | Accepted |
| D-011 | Generate 256-bit local administrator credentials, keep client material only in the OS secret store, persist a PBKDF2-SHA256 verifier, compare in fixed time, and require it for Ready runtime access. | Accepted |
| D-012 | Require exact installation versions and the matching local administrator for maintenance mutations; atomically capture a redacted pre-recovery profile before leaving Ready. | Accepted |
| D-013 | Require profile edit preview hashes to bind installation, actor, correlation, target, entity versions, normalized effective parameters, and provider evidence; apply always re-evaluates before an atomic commit. | Accepted |
| D-014 | Restore only hash- and audit-proven rollback artifacts in `Configuring`; require identical entity topology, current secret/capability/policy validation, and a second hash-bound preview before atomic apply. | Accepted |
| D-015 | Keep environment discovery passive: read bounded native/registry/proc/sysfs/PATH metadata only, classify unknowns conservatively, and defer all candidate execution to the restricted executor. | Accepted |
| D-016 | Build canonical authorization contexts outside model control; deny missing or ambiguous rules; bind administrator decisions to exact hashed invocation identity; persist only hashes/redacted audit evidence; and scope idempotency to one installation. | Accepted |
| D-017 | Expose a restricted-host process kernel only behind `ISandbox`; report enforceable controls exactly, reject unsupported isolation without fallback, and keep generic invocation private until authoritative descriptors, current policy, approval consumption, and audit are one service boundary. | Accepted |
| D-018 | Admit callable tools only as immutable typed exact versions; hash normalized descriptors, progressively disclose summaries before exact descriptions, and never treat catalog admission or inventory as execution or availability proof. | Accepted |
| D-019 | Accept only typed tool parameter values at invocation; derive authority and process settings from the exact descriptor hash; atomically consume approval, persist idempotency, and audit before sandbox start; never replay an uncertain authorized record. | Accepted |
| D-020 | Represent availability checks as explicit inventory-only catalog operations; require denied networking and reported network isolation, exact approval, bounded literal version/help arguments, and full-line redaction before exposing a small observed summary. | Accepted |
| D-021 | Own provider-neutral model requests, artifact-backed content, capability evidence, usage/errors, and sequenced events in Domain/Abstractions; keep vendor SDKs behind adapters and require deterministic providers before live integrations. | Accepted |
| D-022 | Expose an early loopback-only, read-only status preview using same-origin diagnostics and local static assets; keep all web setup mutations and credential entry disabled until the authenticated wizard gate. | Accepted |
| D-023 | Require a versioned immutable context-redaction snapshot for every external model adapter call; bind hosted bearer authorization to an exact HTTPS provider profile and secret reference, and materialize/clear it only around the HTTP send. | Accepted |
| D-024 | Route exact model profiles through a fixed capability/locality/policy/context/tool sequence; prefer a viable primary, rank approved fallbacks deterministically, and bind each selection to immutable evidence without exposing an invocation surface. | Accepted |
| D-025 | Issue only short-lived model route plans after prepared context, serializable durable authority reads, bounded current health, agent-budget checks, and a second race-detection read; keep plans non-authorizing until durable run/audit reservation. | Accepted |
| D-026 | Admit model work only by atomically persisting an idempotent run, first attempt, exact plan/context hashes, token/tool/time reservation, and redacted audit evidence; store no model context and perform no provider egress at admission. | Accepted |
| D-027 | Start model work only after current authority and route revalidation, exact catalog resolution, and an atomic random-hash lease plus shared agent-budget reservation; accept only bounded contiguous provider events, persist their hash/usage rather than content, and reconcile the ledger in the terminal transaction. | Accepted |
| D-028 | Heartbeats require exact hash-bound lease possession and cannot extend expiry; expired recovery atomically records retryable failure, releases the reservation, writes bounded observed provider health, and appends audit evidence without a raw token. | Accepted |
| D-029 | Bind one-to-eight attempts into admission and total agent budget; append only exact ordered retry attempts, exclude every tried profile, reapply current routing/locality/fallback policy, and accumulate run usage/cost/stream/wall evidence while reconciling the ledger per attempt. | Accepted |
| D-030 | Represent the agent loop as six pure typed phases with append-only hash-chained snapshots; resume only exact idempotency/authority matches, require progress evidence at Persist, and stop on bounded repair, no-progress, cancellation, or total-budget outcomes. | Accepted |
| D-031 | Enforce provider data location inside the HTTP socket connect callback: resolve once, reject every mixed/disallowed DNS answer, connect directly to an approved IP, and retain TLS authentication for the exact configured hostname. | Accepted |
| D-032 | Preserve OpenAI, DeepSeek, vLLM, and generic-compatible provider identities over the common hardened wire adapter; validate their secret and transport during shared host/CLI setup without guessing unprobed tool or media capabilities. | Accepted |
| D-033 | Translate Anthropic Messages as a distinct bounded protocol; require exact HTTPS/cloud destination policy, prepared context, invocation-scoped API-key materialization, listed tools, usage, and terminal message evidence. | Accepted |
| D-034 | Own durable task DAGs as immutable hash-chained snapshots with exact-version, hash-only worker leases, bounded retry/compensation, atomic audit, and no completed-node replay. | Accepted |
| D-035 | Derive child grants only by intersecting explicit parent context, capability, budget, policy, skill, depth, count, concurrency, and expiry authority; persist the immutable canonical result. | Accepted |
| D-036 | Calculate bounded recurrence in a pinned exact timezone, persist hash-chained schedule snapshots, and make DST, misfire, overlap, jitter, retry, pause, run-now, expiry, and dead-letter policy explicit. | Accepted |
| D-037 | Store every validated skill version as one canonical content-addressed bundle; expose Markdown only through exact immutable run snapshots; govern the single active-version pointer through deterministic evaluation, separate approval, canary, and atomic promotion/rollback. | Accepted |
| D-038 | Perform coding in exact-baseline Git worktrees, treat repository discovery and Roslyn/MSBuild navigation as bounded read-only evidence, and keep patching, verification, policy, and durable state behind harness-owned contracts. | Accepted |
| D-039 | Accept backend output only as canonical baseline/file-hash-bound unified patches; preflight the complete set before contained writes; keep every verifier harness-owned and require denied-network container/filesystem isolation for project execution. | Accepted |

Detailed technical rationale is recorded in `docs/adr/`.
