# AgentForge Threat Model

## Security objective

Model output and external content may propose actions but cannot authorize them.
Deterministic policy, typed contracts, approval binding, containment, and verification
must mediate every significant effect.

## Assets

- Operator identity, credentials, provider secrets, and approval decisions.
- Workspaces, source repositories, device access, messages, schedules, and network reachability.
- Task state, audit trails, trajectories, skill/plugin packages, evaluators, and active-version pointers.
- Budgets, model routing, policies, configuration, and release artifacts.

## Trust boundaries

1. Operator/CLI/browser to the local control plane.
2. Control plane to application/domain services.
3. Runtime to models, tools, plugins, MCP servers, search, and channels.
4. Safe host to sandboxed processes and coding worktrees.
5. Device discovery to serial open/read/write operations.
6. Relational state to content-addressed artifacts, backups, and exports.
7. Active immutable versions to proposal/canary workspaces.

## Threats and required controls

| Threat | Required controls | First verification |
|---|---|---|
| Direct/indirect prompt injection | Content boundaries, no policy from content, capability checks outside model, output schemas | M2 security fixtures |
| Tool-description poisoning | Signed/provenance descriptors, description treated as data, progressive disclosure | M2 |
| Shell/argument injection | Direct executable plus argument list, no interpolated shell, allowlisted environment | M2 |
| Path traversal and symlink escape | Canonical containment, no untrusted symlink following, hash-bound paths | M2/M5 |
| Secret exfiltration | References, invocation scope, egress policy, redaction before every sink | M1/M2 |
| SSRF/network pivot | Destination policy, DNS/IP recheck, local/metadata address denial, proxy controls | M2/M7 |
| Malicious skills/plugins | Immutable packages, static scan, permission diff, sandbox, signatures, canary/rollback | M5/M10 |
| Supply-chain compromise | Central locks, signatures/hashes, vulnerability and secret scans, SBOM | M0/M10 |
| Privilege escalation | Least-privilege host, explicit privileged capability, no self-grant | M2 |
| Cross-agent/workspace leakage | Scope IDs on records, context minimization, repository filters, authorization tests | M1-M4 |
| Audit tampering | Append-only sequence, chained hashes, external export verification | M1 |
| Provider compromise/outage | Schema validation, bounded retries, health evidence, locality-aware fallback | M3 |
| Runaway loop/cost exhaustion | Budgets, cancellation, repeated-call and no-progress detection | M3/M4 |
| Unsafe self-modification/evaluator capture | Isolated proposals, role separation, deterministic veto, operator approval for core | M5/M9 |
| Webhook replay/identity spoofing | Signature verification, inbox idempotency, stable identity mapping | M7 |
| Hostile media/attachments | Content-type verification, size/decompression bounds, isolated parsing | M3/M7 |
| Hostile device bytes/physical effects | Passive discovery, separate read/write grants, bounded capture, no device text as instructions | M8 |

## M0 controls and limitations

Implemented now: loopback default, constrained correlation IDs, fail-closed startup,
unreadable-state recovery, immutable domain transitions, and dependency enforcement.
No tool execution, model calls, secrets, plugins, channels, schedules, or devices are
enabled, so their attack surfaces remain closed.

Open risks are tracked in `PROJECT_STATE.md`; unrestricted execution cannot be enabled
until M2 controls and security tests pass.
