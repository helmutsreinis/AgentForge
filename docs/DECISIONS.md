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

Detailed technical rationale is recorded in `docs/adr/`.
