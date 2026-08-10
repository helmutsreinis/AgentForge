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

## M1 durable-foundation update

SQLite is initialized through checked-in migrations in WAL mode. Installation writes
use numeric optimistic concurrency, artifacts use canonical SHA-256 paths, and audit
events carry a global sequence plus previous/current hash. File and directory names
are configuration-validated, test databases disable connection pooling for reliable
cold backup and cleanup, and the host migrates before accepting requests.

The Linux gate detected the high-severity SQLitePCLRaw 2.1.11 advisory. AgentForge
now directly pins stable bundle 2.1.12 and fails restore on vulnerability warnings.
At the M1-01 gate, the structured redactor was not yet connected, so only the
explicit `RedactedData` type could cross the audit-journal boundary.

## M1 audit/redaction update

The Audit application service is now the raw-payload entry point. It uses the
Security module to recursively redact sensitive property names and common credential
shapes, sorts JSON object properties for deterministic evidence, rejects oversized or
over-deep payloads, and passes only `RedactedData` to persistence. Hash inputs use
length prefixes, preventing field-boundary ambiguity, and verification reports the
first non-contiguous, relinked, or content-tampered event.

This control currently covers the audit sink. Provider secrets remain disabled until
secret-reference storage and OS-backed invocation materialization pass their gate;
model-context and export redaction must reuse this boundary before those sinks open.

## M1 setup-service update

Setup mutations now pass through one application service. It rejects empty IDs,
oversized identifiers, control characters, mismatched durable installation IDs, and
invalid state transitions before committing. Installation state and its redacted
audit event share one EF unit of work; stale commits return a typed retryable conflict.
CLI options use exact names, reject duplicates and unknown arguments, use direct .NET
APIs rather than a shell, and report storage failures without echoing exception data.

This is not authorization: local administrator credentials and authenticated control
plane mutations are still absent, and the host cannot enter normal mode.

## M1 secrets/provider update

Durable provider rows contain secret store/key references only. Windows credential
bytes are UTF-8 encoded into a temporary buffer, protected with current-user DPAPI,
written atomically, and cleared; materialization decrypts into a disposable character
lease and clears intermediate bytes. Linux Secret Service uses an absolute known
executable, argument arrays, allowlisted DBus environment, redirected stdin, bounded
output, a ten-second timeout, and process-tree termination. Missing tooling returns
`UnsupportedCapability` without degradation.

Provider endpoints reject user info, query strings, and fragments so credentials
cannot be smuggled into configuration. Profiles are installation-scoped, uniquely
named, versioned, and foreign-key bound. Tests scan the database, audit payloads, and
the protected file for the exact plaintext fixture.

## M1 agent-policy update

Agent candidates are normalized and structurally validated before durable state is
read. A selected provider must belong to the same installation and carry observed
text capability. `LocalOnly` requires a loopback endpoint and forbids fallback.
Child depth, count, concurrency, and token allocation are jointly bounded and cannot
exceed the parent bootstrap token budget. Learning mode and mutable-skill scope must
match; bootstrap never grants direct credential access, external messaging, device
write, privileged execution, external network, or autonomous promotion.

Effective previews enumerate explicit `Allow`, `Deny`, and `RequireApproval`
decisions outside model control. Exact tool/skill identifiers remain approval-gated
because their catalogs are not available yet. Preview is read-only, while create
re-evaluates and atomically appends a redacted audit event. M2 must still implement
request-bound authorization, inheritance/intersection, approval expiration, and the
global missing-policy-denies evaluator used at invocation time.

## M1 administrator/completion update

AgentForge generates administrator credentials from 256 random bits and encodes them
without creating a managed plaintext string. The client credential is stored through
the OS adapter; SQLite stores only store/key reference, random salt, work factor, and
PBKDF2-SHA256 verifier. Verification uses fixed-time comparison. Tests materialize
the client reference for one disposable lease and prove exact credential bytes are
absent from SQLite.

Completion fails closed unless storage migrations have initialized, the global audit
chain verifies, a text provider's secret can materialize, and a named agent exists.
External credential creation precedes the relational commit, so any unsuccessful
commit triggers exact-reference deletion. A Ready runtime endpoint returns 401
without a valid bounded bearer credential. Rate limits, lockout/session policy,
request idempotency, CSRF/browser authentication, and remote TLS remain open and must
pass before exposing mutation or remote surfaces.
